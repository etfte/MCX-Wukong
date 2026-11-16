using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static void Main(string[] args)
    {
        string targetPath = args.Length > 0 ? args[0] : "DqjCombat.dll";
        if (!File.Exists(targetPath))
        {
            targetPath = @"D:\SteamLibrary\steamapps\common\BlackMythWukong\b1\Binaries\Win64\CSharpLoader\Mods\DqjCombat\DqjCombat.dll";
        }

        if (!File.Exists(targetPath))
        {
            Console.WriteLine($"Error: target binary not found: {targetPath}");
            return;
        }

        string bakPath = targetPath + ".bak";
        if (!File.Exists(bakPath))
        {
            File.Copy(targetPath, bakPath, false);
            Console.WriteLine($"Created backup at {bakPath}");
        }

        Console.WriteLine($"Patching {targetPath} ...");

        var readerParams = new ReaderParameters { ReadWrite = true };
        using (var asm = AssemblyDefinition.ReadAssembly(targetPath, readerParams))
        {
            var module = asm.MainModule;

            // Methods to reference from .NET Framework 4.8 mscorlib
            var replaceMethod = module.ImportReference(typeof(string).GetMethod("Replace", new[] { typeof(char), typeof(char) }));
            var getInvariant = module.ImportReference(typeof(System.Globalization.CultureInfo).GetProperty("InvariantCulture").GetGetMethod());
            var doubleParse = module.ImportReference(typeof(double).GetMethod("Parse", new[] { typeof(string), typeof(IFormatProvider) }));
            var toStringMethod = module.ImportReference(typeof(object).GetMethod("ToString", Type.EmptyTypes));

            Action<MethodDefinition> patchMethod = (MethodDefinition m) =>
            {
                m.Body.Instructions.Clear();
                var il = m.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Callvirt, toStringMethod));
                il.Append(il.Create(OpCodes.Ldc_I4, (int)','));
                il.Append(il.Create(OpCodes.Ldc_I4, (int)'.'));
                il.Append(il.Create(OpCodes.Callvirt, replaceMethod));
                il.Append(il.Create(OpCodes.Call, getInvariant));
                il.Append(il.Create(OpCodes.Call, doubleParse));
                il.Append(il.Create(OpCodes.Ret));
                Console.WriteLine($"Patched {m.DeclaringType.Name}.{m.Name}");
            };

            // 1. McBridgeWire.Number
            var wire = module.Types.FirstOrDefault(t => t.Name == "McBridgeWire");
            if (wire != null) patchMethod(wire.Methods.First(m => m.Name == "Number"));

            // 2. McWorldCatalog.N
            var catalog = module.Types.FirstOrDefault(t => t.Name == "McWorldCatalog");
            if (catalog != null) patchMethod(catalog.Methods.First(m => m.Name == "N"));

            // 3. McWorldLink.N
            var link = module.Types.FirstOrDefault(t => t.Name == "McWorldLink");
            if (link != null) patchMethod(link.Methods.First(m => m.Name == "N"));

            // 4. Mod.Init Culture fix
            var mod = module.Types.FirstOrDefault(t => t.Name == "Mod");
            if (mod != null)
            {
                var init = mod.Methods.FirstOrDefault(m => m.Name == "Init");
                if (init != null)
                {
                    var setCulture = module.ImportReference(typeof(System.Globalization.CultureInfo).GetProperty("DefaultThreadCurrentCulture").GetSetMethod());
                    var il = init.Body.GetILProcessor();
                    var first = init.Body.Instructions.First();
                    il.InsertBefore(first, il.Create(OpCodes.Call, getInvariant));
                    il.InsertBefore(first, il.Create(OpCodes.Call, setCulture));
                    Console.WriteLine("Patched Mod.Init culture");
                }
            }

            asm.Write();
            Console.WriteLine("Assembly saved successfully!");
        }
    }
}
