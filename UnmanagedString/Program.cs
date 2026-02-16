using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using dnlib.PE;
using System;
using System.IO;

namespace UnmanagedStringDnlib
{
    internal static class Program
    {
        private static string filePath = string.Empty, outputFilePath = string.Empty;
        private static ModuleDefMD Module = null;
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

            Module.Cor20HeaderFlags &= ~ComImageFlags.ILOnly;
            if (isX86)
                Module.Cor20HeaderFlags |= ComImageFlags.Bit32Required;

            int hidden = StringHider.Execute(Module, isX86);

            MWO = new ModuleWriterOptions(Module);
            MWO.PEHeadersOptions.Machine = isX86 ? Machine.I386 : Machine.AMD64;
            MWO.Cor20HeaderOptions.Flags = Module.Cor20HeaderFlags;

            StringHider.AttachWriter(MWO);

            Module.Write(outputFilePath, MWO);

            Console.WriteLine($"Hidden {hidden} strings\nOutput: {Path.GetFileName(outputFilePath)}");
            Console.ReadLine();
        }
    }
}