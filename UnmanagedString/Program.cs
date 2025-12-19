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
using UnmanagedString;

namespace UnmanagedStringDnlib
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

            bool isX86 = Module.Machine == Machine.I386;

            Importer importer = new Importer(Module);

            Type sbytePtr = typeof(sbyte).MakePointerType(), charPtr = typeof(char).MakePointerType();

            IMethod stringSbytePtrCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr })!),
                    stringCharPtrCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr })!),
                    stringSbytePtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr, typeof(int), typeof(int) })!),
                    stringCharPtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr, typeof(int), typeof(int) })!);

            Module.Cor20HeaderFlags &= ~ComImageFlags.ILOnly;
            if (isX86) Module.Cor20HeaderFlags |= ComImageFlags.Bit32Required;

            NativeMethodInjector Injector = new NativeMethodInjector();
            var nativeByKey = new Dictionary<(string content, bool unicode, bool addNull), MethodDef>();
            int hidden = 0;

           
            var stringHolder = new TypeDefUser(
               NameGenerator.Next(), 
               NameGenerator.Next(), 
                Module.CorLibTypes.Object.TypeDefOrRef)
            {
                Attributes =
         TypeAttributes.NotPublic |
         TypeAttributes.Abstract |
         TypeAttributes.Sealed |
         TypeAttributes.BeforeFieldInit
            };

            Module.Types.Add(stringHolder);

            foreach (TypeDef type in Module.GetTypes().Where(t => !t.IsGlobalModuleType && t.HasMethods))
            {
                foreach (MethodDef method in type.Methods.Where(m => m.HasBody && m.Body.HasInstructions && m.Body.Instructions.Count > 1))
                {
                    var instructions = method.Body.Instructions;
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        Instruction instruction = instructions[i];
                        if (instruction.OpCode != OpCodes.Ldstr) continue;
                        if (instruction.Operand is not string s || string.IsNullOrWhiteSpace(s)) continue;

                        string content = s;
                        bool useUnicode = !CanBeEncodedIn7BitAscii(content);
                        bool addNullTerminator = !HasNullCharacter(content);
                        var key = (content, useUnicode, addNullTerminator);

                        if (!nativeByKey.TryGetValue(key, out var native))
                        {
                            native = CreateNativePointerMethod(Module, Injector, content, isX86, useUnicode, addNullTerminator);
                            nativeByKey[key] = native;
                        }

                       
                        var wrapper = CreateManagedStringMethod(stringHolder, native, useUnicode, stringSbytePtrCtor, stringCharPtrCtor, stringSbytePtrLenCtor, stringCharPtrLenCtor, content, addNullTerminator);

                        
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = wrapper;
                        hidden++;
                    }

                    if (hidden > 0)
                    {
                        method.Body.SimplifyBranches();
                        method.Body.OptimizeBranches();
                    }
                }
            }

            MWO = new ModuleWriterOptions(Module);
            MWO.PEHeadersOptions.Machine = isX86 ? Machine.I386 : Machine.AMD64;
            MWO.Cor20HeaderOptions.Flags = Module.Cor20HeaderFlags;
            Injector.AttachToWriter(MWO);

            Module.Write(outputFilePath, MWO);
            Console.WriteLine($"Hidden {hidden} strings\nOutput: {Path.GetFileName(outputFilePath)}");
            Console.ReadLine();
        }

        private static MethodDef CreateManagedStringMethod(
            TypeDef holder,
            MethodDef nativeMethod,
            bool useUnicode,
            IMethod stringSbytePtrCtor,
            IMethod stringCharPtrCtor,
            IMethod stringSbytePtrLenCtor,
            IMethod stringCharPtrLenCtor,
            string content,
            bool addNullTerminator)
        {
            var sig = MethodSig.CreateStatic(holder.Module.CorLibTypes.String);
            var method = new MethodDefUser(NameGenerator.Next(), sig, MethodImplAttributes.IL, MethodAttributes.Assembly | MethodAttributes.Static);
            var body = new CilBody();
            method.Body = body;

            body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, nativeMethod));

            var calliSig = MethodSig.CreateStatic(
                useUnicode ? new PtrSig(holder.Module.CorLibTypes.Char) : new PtrSig(holder.Module.CorLibTypes.SByte));
            calliSig.CallingConvention = CallingConvention.Unmanaged;

            body.Instructions.Add(Instruction.Create(OpCodes.Calli, calliSig));

            if (addNullTerminator)
            {
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj, useUnicode ? stringCharPtrCtor : stringSbytePtrCtor));
            }
            else
            {
                body.Instructions.Add(Instruction.CreateLdcI4(0));
                body.Instructions.Add(Instruction.CreateLdcI4(content.Length));
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj, useUnicode ? stringCharPtrLenCtor : stringSbytePtrLenCtor));
            }

            body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            holder.Methods.Add(method);
            return method;
        }

        private static MethodDef CreateNativePointerMethod(ModuleDef Module, NativeMethodInjector Injector, string content, bool isX86, bool useUnicode, bool addNullTerminator)
        {
            if (addNullTerminator) content += "\0";
            byte[] bytes = useUnicode ? Encoding.Unicode.GetBytes(content) : Encoding.ASCII.GetBytes(content);
            TypeSig ret = useUnicode ? new PtrSig(Module.CorLibTypes.Char) : new PtrSig(Module.CorLibTypes.SByte);
            MethodSig sig = MethodSig.CreateStatic(ret);

            string name = "" + NameGenerator.Next();
            MethodDefUser m = new MethodDefUser(name, sig,
                MethodImplAttributes.Native | MethodImplAttributes.Unmanaged | MethodImplAttributes.PreserveSig,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl);
            m.ImplMap = new ImplMapUser(
               new ModuleRefUser(Module, "SKIDDED.dll"),
               name,
                PInvokeAttributes.CallConvStdCall |
                PInvokeAttributes.NoMangle
         );
            Module.GlobalType.Methods.Add(m);

            byte[] prefix = isX86
                ? new byte[]
                {
                 0x55, // push ebp
                0x89, 0xE5, // mov ebp, esp
                0xE8, 0x05, 0x00, 0x00, 0x00, // call <jump1>
                0x83, 0xC0, 0x01, // add eax, 1
                // <jump2>:
               0x5D, // pop ebp
                0xC3, // ret
                // <jump1>:
               0x58, // pop eax
                0x83, 0xC0, 0x0B, // add eax, 0xb
                0xEB, 0xF8 // jmp <jump2>
                }
                : new byte[]
                {
                0x48, 0x8D, 0x05, 0x01, 0x00, 0x00, 0x00, // lea rax, [rip+1] 0xC3
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
                if (text[i] > '\x7f') return false;
            return true;
        }

        private static bool HasNullCharacter(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\0') return true;
            return false;
        }

        internal static class NameGenerator
        {
            private static int _counter = 0;

            public static string Next()
            {
                int value = _counter++;
                StringBuilder sb = new StringBuilder();

                do
                {
                    sb.Insert(0, (char)('A' + (value % 26)));
                    value = (value / 26) - 1;
                }
                while (value >= 0);

                return sb.ToString();
            }
        }
    }
}

