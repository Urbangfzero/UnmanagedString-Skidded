using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using System;
using System.Collections.Generic;

namespace UnmanagedString
{
    /// <summary>
    /// Injects native code bytes into the PE and patches the target Method RVA in the Method table.
    /// </summary>
    internal sealed class NativeMethodInjector
    {
        private sealed class Item
        {
            public MethodDef Method;
            public byte[] Code;
            public MethodBody? Chunk;

            public Item(MethodDef method, byte[] code)
            {
                Method = method;
                Code = code;
                Chunk = null;
            }
        }

        private readonly List<Item> _items = new();

        public void Register(MethodDef method, byte[] code) => _items.Add(new Item(method, code));

        public void AttachToWriter(ModuleWriterOptions opts) => opts.WriterEvent += OnWriterEvent;

        private void OnWriterEvent(object? sender, ModuleWriterEventArgs e)
        {
            ModuleWriterBase writer = e.Writer;
            switch (e.Event)
            {
                case ModuleWriterEvent.MDEndWriteMethodBodies:
                    {
                        // Create Method body chunks for each native code blob
                        for (int i = 0; i < _items.Count; i++)
                        {
                            Item item = _items[i];
                            MethodBody chunk = writer.MethodBodies.Add(new MethodBody(item.Code));
                            item.Chunk = chunk;
                        }
                        break;
                    }

                case ModuleWriterEvent.EndCalculateRvasAndFileOffsets:
                    {
                        // Patch MethodDef RVA in Method table for each injected native Method
                        foreach (Item item in _items)
                        {
                            if (item.Chunk is null)
                                throw new InvalidOperationException("Native chunk was not created.");

                            uint rid = writer.Metadata.GetRid(item.Method);
                            if (rid == 0)
                                throw new InvalidOperationException("Method RID is 0 (not in metadata).");

                            MDTable<RawMethodRow> table = writer.Metadata.TablesHeap.MethodTable;
                            RawMethodRow row = table[rid];

                            table[rid] = new RawMethodRow(
                                (uint)item.Chunk.RVA,
                                row.ImplFlags,
                                row.Flags,
                                row.Name,
                                row.Signature,
                                row.ParamList
                            );
                        }
                        break;
                    }
            }
        }
    }
}