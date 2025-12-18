using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using dnlib.PE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UnmanagedString
{
    internal static class Program
    {
        static string filePath = string.Empty, outputFilePath = string.Empty;
        static ModuleDefMD Module = null;
        public static ModuleWriterOptions MWO = null;
        private static void Main(string[] args)
        {
            if (args.Length != 0)
                filePath = args[0].Trim('"');
            
            while (!File.Exists(filePath))
            {
                Console.WriteLine("File Path: ");
                filePath = Console.ReadLine().Trim('"');
                Console.Clear();
            }

            outputFilePath = filePath.Insert(filePath.Length - 4, "-US");

            Module = ModuleDefMD.Load(filePath);

            // Decide target arch from PE machine
            bool isX86 = Module.Machine == Machine.I386;

            // Import string pointer ctors (unsafe pointer signatures via reflection)
            Importer importer = new Importer(Module);

            Type sbytePtr = typeof(sbyte).MakePointerType(), charPtr = typeof(char).MakePointerType();

            IMethod stringSbytePtrCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr })!),
                stringCharPtrCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr })!),
                stringSbytePtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr, typeof(int), typeof(int) })!),
                stringCharPtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr, typeof(int), typeof(int) })!);

            // Make sure ILOnly is cleared if you’re adding native Method bodies.
            Module.Cor20HeaderFlags &= ~ComImageFlags.ILOnly;

            if (isX86)
                Module.Cor20HeaderFlags |= ComImageFlags.Bit32Required;

            // Deduplicate by (content, unicode?, addNull?)
            var nativeByKey = new Dictionary<(string content, bool unicode, bool addNull), MethodDef>();

            // Collect native injections, and wire them up through writer events.
            NativeMethodInjector Injector = new NativeMethodInjector();

            int hidden = 0;

            foreach (TypeDef Type in Module.GetTypes().Where(T => !T.IsGlobalModuleType && T.HasMethods))
                foreach (MethodDef Method in Type.Methods.Where(M => M.HasBody && M.Body.HasInstructions && M.Body.Instructions.Count() > 1))
                {
                    var Instructions = Method.Body.Instructions;
                    for (int i = 0; i < Instructions.Count; i++)
                    {
                        Instruction Instruction = Instructions[i];
                        if (Instruction.OpCode != OpCodes.Ldstr)
                            continue;

                        if (Instruction.Operand is not string s || s.Length == 0 || s == "" || s == " ")
                            continue;

                        string content = s;

                        bool useUnicode = !CanBeEncodedIn7BitAscii(content), addNullTerminator = !HasNullCharacter(content);

                        var key = (content, useUnicode, addNullTerminator);
                        if (!nativeByKey.TryGetValue(key, out var native))
                        {
                            native = CreateNativePointerMethod(Module, Injector, content, isX86, useUnicode, addNullTerminator);
                            nativeByKey[key] = native;
                        }

                        // Replace: ldstr => call native() => newobj string(...)
                        // Stack after call: pointer
                        Instruction.OpCode = OpCodes.Call;
                        Instruction.Operand = native;

                        // IMPORTANT: String(sbyte*) expects null-terminated.
                        // If we didn't add a null terminator, use ctor(ptr, startIndex, length).
                        if (addNullTerminator)
                        {
                            Instructions.Insert(++i, Instruction.Create(OpCodes.Newobj, useUnicode ? (IMethod)stringCharPtrCtor : stringSbytePtrCtor));
                        }
                        else
                        {
                            Instructions.Insert(++i, Instruction.CreateLdcI4(0));
                            Instructions.Insert(++i, Instruction.CreateLdcI4(content.Length));
                            Instructions.Insert(++i, Instruction.Create(OpCodes.Newobj, useUnicode ? (IMethod)stringCharPtrLenCtor : stringSbytePtrLenCtor));
                        }

                        hidden++;
                    }

                    if (hidden > 0)
                    {
                        Method.Body.SimplifyBranches();
                        Method.Body.OptimizeBranches();
                    }
                }

            // Write with RVA patching via writer events
            MWO = new ModuleWriterOptions(Module);

            // Force PE machine to match our decision (helps ESPECIALLY if input was AnyCPU)
            MWO.PEHeadersOptions.Machine = isX86 ? Machine.I386 : Machine.AMD64;

            // Keep flags consistent
            MWO.Cor20HeaderOptions.Flags = Module.Cor20HeaderFlags;

            Injector.AttachToWriter(MWO);

            Module.Write(outputFilePath, MWO);

            Console.WriteLine($"Hidden {hidden} strings\nOutput: {Path.GetFileName(outputFilePath)}");

            Console.ReadLine();
        }

        private static MethodDef CreateNativePointerMethod(ModuleDef Module, NativeMethodInjector Injector, string content, bool isX86, bool useUnicode, bool addNullTerminator)
        {
            if (addNullTerminator)
                content += "\0"; // encoding-dependent width

            byte[] bytes = useUnicode ? Encoding.Unicode.GetBytes(content) : Encoding.ASCII.GetBytes(content);

            // Return Type: pointer to SByte or Char
            TypeSig ret = useUnicode ? new PtrSig(Module.CorLibTypes.Char) : new PtrSig(Module.CorLibTypes.SByte);

            MethodSig sig = MethodSig.CreateStatic(ret);

            string name = Guid.NewGuid().ToString("N");
            MethodDefUser m = new MethodDefUser(name, sig, MethodImplAttributes.Native | MethodImplAttributes.Unmanaged | MethodImplAttributes.PreserveSig, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl);

            Module.GlobalType.Methods.Add(m);

            byte[] prefix = isX86
                ? new byte[]
                {
                0x55,             // push ebp
                0x89, 0xE5,       // mov ebp, esp
                0xE8, 0x05, 0, 0, 0, // call +5 (<jump1>)
                0x83, 0xC0, 0x01, // add eax, 1
                // <jump2>:
                0x5D,             // pop ebp
                0xC3,             // ret
                // <jump1>:
                0x58,             // pop eax
                0x83, 0xC0, 0x0B, // add eax, 0x0B
                0xEB, 0xF8        // jmp -8 (<jump2>)
                }
                : new byte[]
                {
                0x48, 0x8D, 0x05, 0x01, 0x00, 0x00, 0x00, // lea rax, [rip+1]
                0xC3                                        // ret
                };

            byte[] code = new byte[prefix.Length + bytes.Length];
            Buffer.BlockCopy(prefix, 0, code, 0, prefix.Length);
            Buffer.BlockCopy(bytes, 0, code, prefix.Length, bytes.Length);

            Injector.Register(m, code);

            return m;
        }

        private static bool CanBeEncodedIn7BitAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] > '\x7f')
                    return false;

            return true;
        }

        private static bool HasNullCharacter(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\0')
                    return true;

            return false;
        }
    }
}