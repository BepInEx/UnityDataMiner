using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace UnityDataMiner
{
    public static class AssemblyStripper
    {
        public static void StripAssembly(string path)
        {
            using var module = ModuleDefMD.Load(File.ReadAllBytes(path));
            foreach (var typeDef in module.GetTypes())
            {
                Strip(typeDef);
                Publicize(typeDef);
            }

            module.Write(path);
        }

        private static void Publicize(TypeDef td)
        {
            td.Attributes &= ~TypeAttributes.VisibilityMask;
            if (td.IsNested)
                td.Attributes |= TypeAttributes.NestedPublic;
            else
                td.Attributes |= TypeAttributes.Public;

            foreach (var methodDef in td.Methods)
            {
                if (methodDef.IsCompilerControlled)
                    continue;

                methodDef.Attributes &= ~MethodAttributes.MemberAccessMask;
                methodDef.Attributes |= MethodAttributes.Public;
            }

            var eventNames = td.Events.Select(e => e.Name).ToHashSet();
            foreach (var fieldDef in td.Fields)
            {
                if (fieldDef.IsCompilerControlled)
                    continue;

                // Skip event backing fields
                if (eventNames.Contains(fieldDef.Name))
                    continue;

                fieldDef.Attributes &= ~FieldAttributes.FieldAccessMask;
                fieldDef.Attributes |= FieldAttributes.Public;
            }
        }

        private static void Strip(TypeDef td)
        {
            if (td.IsEnum || td.IsInterface)
                return;

            foreach (var methodDef in td.Methods)
            {
                if (!methodDef.HasBody)
                    continue;
                var newBody = new CilBody();
                newBody.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                newBody.Instructions.Add(Instruction.Create(OpCodes.Throw));
                methodDef.Body = newBody;
                methodDef.IsAggressiveInlining = false;
                methodDef.IsNoInlining = true;
            }
        }
    }
}