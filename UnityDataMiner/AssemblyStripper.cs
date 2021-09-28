using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using FieldAttributes = dnlib.DotNet.FieldAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;

namespace UnityDataMiner
{
    public static class AssemblyStripper
    {
        private static readonly ConstructorInfo AttributeCtor = typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
        
        public static void StripAssembly(string path)
        {
            using var module = ModuleDefMD.Load(File.ReadAllBytes(path));

            var (pubType, pubCtor) = CreatePublicizedAttribute(module);
            
            foreach (var typeDef in module.GetTypes())
            {
                if (typeDef == pubType)
                    continue;

                Strip(typeDef);
                Publicize(typeDef, pubCtor);
            }

            module.Write(path);
        }

        private static (TypeDef, MethodDef) CreatePublicizedAttribute(ModuleDef module)
        {
            TypeDef td = new TypeDefUser("System.Runtime.CompilerServices", "PublicizedAttribute", module.Import(typeof(Attribute)));
            td.Attributes |= TypeAttributes.Public | TypeAttributes.Sealed;
            MethodDef ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void),
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Public);
            td.Methods.Add(ctor);
            ctor.Body = new CilBody();
            ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(module.Import(AttributeCtor)));
            ctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            module.Types.Add(td);
            return (td, ctor);
        }

        private static void Publicize(TypeDef td, ICustomAttributeType pubAttribute)
        {
            if (!td.IsPublic || (td.IsNested && !td.IsNestedPublic))
                td.CustomAttributes.Add(new CustomAttribute(pubAttribute));
            
            td.Attributes &= ~TypeAttributes.VisibilityMask;
            if (td.IsNested)
                td.Attributes |= TypeAttributes.NestedPublic;
            else
                td.Attributes |= TypeAttributes.Public;

            foreach (var methodDef in td.Methods)
            {
                if (methodDef.IsCompilerControlled)
                    continue;
                
                if (!methodDef.IsPublic)
                    methodDef.CustomAttributes.Add(new CustomAttribute(pubAttribute));

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
                
                if (!fieldDef.IsPublic)
                    fieldDef.CustomAttributes.Add(new CustomAttribute(pubAttribute));

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
