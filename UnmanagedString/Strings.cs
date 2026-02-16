using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnmanagedString;

namespace UnmanagedStringDnlib
{
    internal static class StringHider
    {
        private static NativeMethodInjector Injector;

        public static int Execute(ModuleDefMD Module, bool isX86)
        {
            Importer importer = new Importer(Module);

            Type sbytePtr = typeof(sbyte).MakePointerType();
            Type charPtr = typeof(char).MakePointerType();

            IMethod stringSbytePtrCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr })!);
            IMethod stringCharPtrCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr })!);
            IMethod stringSbytePtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { sbytePtr, typeof(int), typeof(int) })!);
            IMethod stringCharPtrLenCtor = importer.Import(typeof(string).GetConstructor(new[] { charPtr, typeof(int), typeof(int) })!);

            Injector = new NativeMethodInjector();

            var nativeByKey = new Dictionary<(string, bool, bool), MethodDef>();
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

            foreach (var type in Module.GetTypes().Where(t => !t.IsGlobalModuleType && t.HasMethods))
            {
                foreach (var method in type.Methods.Where(m => m.HasBody && m.Body.HasInstructions))
                {
                    var instructions = method.Body.Instructions;

                    for (int i = 0; i < instructions.Count; i++)
                    {
                        if (instructions[i].OpCode != OpCodes.Ldstr)
                            continue;

                        if (instructions[i].Operand is not string content || string.IsNullOrWhiteSpace(content))
                            continue;

                        bool useUnicode = !CanBeEncodedIn7BitAscii(content);
                        bool addNullTerminator = !HasNullCharacter(content);

                        var key = (content, useUnicode, addNullTerminator);

                        if (!nativeByKey.TryGetValue(key, out var native))
                        {
                            native = CreateNativePointerMethod(Module, content, isX86, useUnicode, addNullTerminator);
                            nativeByKey[key] = native;
                        }

                        var wrapper = CreateManagedStringMethod(
                            stringHolder,
                            native,
                            useUnicode,
                            stringSbytePtrCtor,
                            stringCharPtrCtor,
                            stringSbytePtrLenCtor,
                            stringCharPtrLenCtor,
                            content,
                            addNullTerminator);

                        instructions[i].OpCode = OpCodes.Call;
                        instructions[i].Operand = wrapper;

                        hidden++;
                    }

                    method.Body.SimplifyBranches();
                    method.Body.OptimizeBranches();
                }
            }

            return hidden;
        }

        public static void AttachWriter(ModuleWriterOptions options)
        {
            Injector.AttachToWriter(options);
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
            var method = new MethodDefUser(NameGenerator.Next(), sig,
                MethodImplAttributes.IL,
                MethodAttributes.Assembly | MethodAttributes.Static);

            var body = new CilBody();
            method.Body = body;

            body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, nativeMethod));

            var calliSig = MethodSig.CreateStatic(
                useUnicode
                    ? new PtrSig(holder.Module.CorLibTypes.Char)
                    : new PtrSig(holder.Module.CorLibTypes.SByte));

            calliSig.CallingConvention = CallingConvention.Unmanaged;

            body.Instructions.Add(Instruction.Create(OpCodes.Calli, calliSig));

            if (addNullTerminator)
            {
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj,
                    useUnicode ? stringCharPtrCtor : stringSbytePtrCtor));
            }
            else
            {
                body.Instructions.Add(Instruction.CreateLdcI4(0));
                body.Instructions.Add(Instruction.CreateLdcI4(content.Length));
                body.Instructions.Add(Instruction.Create(OpCodes.Newobj,
                    useUnicode ? stringCharPtrLenCtor : stringSbytePtrLenCtor));
            }

            body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            holder.Methods.Add(method);
            return method;
        }

        private static MethodDef CreateNativePointerMethod(
            ModuleDef Module,
            string content,
            bool isX86,
            bool useUnicode,
            bool addNullTerminator)
        {
            if (addNullTerminator)
                content += "\0";

            byte[] bytes = useUnicode
                ? Encoding.Unicode.GetBytes(content)
                : Encoding.ASCII.GetBytes(content);

            TypeSig ret = useUnicode
                ? new PtrSig(Module.CorLibTypes.Char)
                : new PtrSig(Module.CorLibTypes.SByte);

            MethodSig sig = MethodSig.CreateStatic(ret);

            string name = NameGenerator.Next();

            var m = new MethodDefUser(name, sig,
                MethodImplAttributes.Native |
                MethodImplAttributes.Unmanaged |
                MethodImplAttributes.PreserveSig,
                MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.PinvokeImpl);

            m.ImplMap = new ImplMapUser(
                new ModuleRefUser(Module, "SKIDDED.dll"),
                name,
                PInvokeAttributes.CallConvStdCall |
                PInvokeAttributes.NoMangle);

            Module.GlobalType.Methods.Add(m);

                    byte[] prefix = isX86
              ? new byte[]
              {
                0x55,                       // push ebp
                0x89, 0xE5,                 // mov ebp, esp
                0x33, 0xC0,                 // xor eax, eax (opaque predicate - always 0)
                0x85, 0xC0,                 // test eax, eax
                0x75, 0x02,
                0x75, 0x02, 0x75, 0x02, 0x75, 0x02, 0xEB, 0x00,  0x75, 0x02, 0x75, 0x02,
                          // jne +2 (never taken)
                0xEB, 0x00,                 // jmp +0 (dead padding)
                // Real control flow:
                0xE8, 0x05, 0x00, 0x00, 0x00, // call +5
                0x83, 0xC0, 0x01,           // add eax, 1
                0x5D,                       // pop ebp
                0xC3,                       // ret
                // Unreachable:
                0x58,                       // pop eax
                0x83, 0xC0, 0x0B,           // add eax, 0x0B
                0xEB, 0xF8                  // jmp -8
              }
              : new byte[]
              {
                0x48, 0x83, 0xFF, 0x00,     // cmp rdi, 0
                0x75, 0x02,                 // jne +2 (never taken)
                0xEB, 0x00,                 // jmp +0 (dead padding)
                0x48, 0x8D, 0x05, 0x01, 0x00, 0x00, 0x00, // lea rax, [rip+1]
                0xC3                        // ret
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