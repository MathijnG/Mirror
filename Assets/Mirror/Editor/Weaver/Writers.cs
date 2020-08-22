using System.Collections.Generic;
using Mono.CecilX;
using Mono.CecilX.Cil;

namespace Mirror.Weaver
{
    public static class Writers
    {
        const int MaxRecursionCount = 128;

        static Dictionary<string, MethodReference> writeFuncs;

        public static void Init()
        {
            writeFuncs = new Dictionary<string, MethodReference>();
        }

        public static void Register(TypeReference dataType, MethodReference methodReference)
        {
            writeFuncs[dataType.FullName] = methodReference;
        }

        static void RegisterWriteFunc(string name, MethodDefinition newWriterFunc)
        {
            writeFuncs[name] = newWriterFunc;
            Weaver.WeaveLists.generatedWriteFunctions.Add(newWriterFunc);

            Weaver.ConfirmGeneratedCodeClass();
            Weaver.WeaveLists.generateContainerClass.Methods.Add(newWriterFunc);
        }

        /// <summary>
        /// Finds existing writer for type, if non exists trys to create one
        /// <para>This method is recursive</para>
        /// </summary>
        /// <param name="variable"></param>
        /// <param name="recursionCount"></param>
        /// <returns>Returns <see cref="MethodReference"/> or throws</returns>
        /// <exception cref="GenerateWriterException">Throws when writer could not be generated for type</exception>
        public static MethodReference GetWriteFunc(TypeReference variable, int recursionCount = 0)
        {
            if (writeFuncs.TryGetValue(variable.FullName, out MethodReference foundFunc))
            {
                return foundFunc;
            }
            else if (variable.Resolve().IsEnum)
            {
                // serialize enum as their base type
                return GetWriteFunc(variable.Resolve().GetEnumUnderlyingType());
            }
            else
            {
                MethodDefinition newWriterFunc = null;

                // this try/catch will be removed in future PR and make `GetWriteFunc` throw instead
                try
                {
                    newWriterFunc = GenerateWriter(variable, recursionCount);
                }
                catch (GenerateWriterException e)
                {
                    Weaver.Error(e.Message, e.MemberReference);
                }

                if (newWriterFunc != null)
                {
                    RegisterWriteFunc(variable.FullName, newWriterFunc);
                }
                return newWriterFunc;
            }
        }

        /// <exception cref="GenerateWriterException">Throws when writer could not be generated for type</exception>
        static MethodDefinition GenerateWriter(TypeReference variableReference, int recursionCount = 0)
        {
            // TODO: do we need this check? do we ever receieve types that are "ByReference"s
            if (variableReference.IsByReference)
            {
                throw new GenerateWriterException($"Cannot pass {variableReference.Name} by reference", variableReference);
            }

            // Arrays are special, if we resolve them, we get the element type,
            // eg int[] resolves to int
            // therefore process this before checks below
            if (variableReference.IsArray)
            {
                return GenerateArrayWriteFunc(variableReference, recursionCount);
            }

            // check for collections

            if (variableReference.IsArraySegment())
            {
                return GenerateArraySegmentWriteFunc(variableReference, recursionCount);
            }
            if (variableReference.IsList())
            {
                return GenerateListWriteFunc(variableReference, recursionCount);
            }

            // check for invalid types

            TypeDefinition VariableDefinition = variableReference.Resolve();
            if (VariableDefinition == null)
            {
                throw new GenerateWriterException($"{variableReference.Name} is not a supported type", variableReference);
            }
            if (VariableDefinition.IsDerivedFrom(WeaverTypes.ComponentType))
            {
                throw new GenerateWriterException($"Cannot generate writer for component type {variableReference.Name}", variableReference);
            }
            if (variableReference.FullName == WeaverTypes.ObjectType.FullName)
            {
                throw new GenerateWriterException($"Cannot generate writer for {variableReference.Name}", variableReference);
            }
            if (variableReference.FullName == WeaverTypes.ScriptableObjectType.FullName)
            {
                throw new GenerateWriterException($"Cannot generate writer for {variableReference.Name}", variableReference);
            }
            if (VariableDefinition.HasGenericParameters)
            {
                throw new GenerateWriterException($"Cannot generate writer for generic type {variableReference.Name}", variableReference);
            }
            if (VariableDefinition.IsInterface)
            {
                throw new GenerateWriterException($"Cannot generate writer for interface {variableReference.Name}", variableReference);
            }

            // generate writer for class/struct

            return GenerateClassOrStructWriterFunction(variableReference, recursionCount);
        }

        static MethodDefinition GenerateClassOrStructWriterFunction(TypeReference variable, int recursionCount)
        {
            if (recursionCount > MaxRecursionCount)
            {
                throw new GenerateWriterException($"{variable.Name} can't be serialized because it references itself", variable);
            }

            string functionName = "_Write" + variable.Name + "_";
            if (variable.DeclaringType != null)
            {
                functionName += variable.DeclaringType.Name;
            }
            else
            {
                functionName += "None";
            }
            // create new writer for this type
            MethodDefinition writerFunc = new MethodDefinition(functionName,
                    MethodAttributes.Public |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    WeaverTypes.voidType);

            writerFunc.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None, Weaver.CurrentAssembly.MainModule.ImportReference(WeaverTypes.NetworkWriterType)));
            writerFunc.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, Weaver.CurrentAssembly.MainModule.ImportReference(variable)));

            ILProcessor worker = writerFunc.Body.GetILProcessor();

            if (!WriteAllFields(variable, recursionCount, worker))
                return null;

            worker.Append(worker.Create(OpCodes.Ret));
            return writerFunc;
        }

        /// <summary>
        /// Find all fields in type and write them
        /// </summary>
        /// <param name="variable"></param>
        /// <param name="recursionCount"></param>
        /// <param name="worker"></param>
        /// <returns>false if fail</returns>
        static bool WriteAllFields(TypeReference variable, int recursionCount, ILProcessor worker)
        {
            uint fields = 0;
            foreach (FieldDefinition field in variable.FindAllPublicFields())
            {
                MethodReference writeFunc = GetWriteFunc(field.FieldType, recursionCount + 1);

                FieldReference fieldRef = Weaver.CurrentAssembly.MainModule.ImportReference(field);

                fields++;
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldarg_1));
                worker.Append(worker.Create(OpCodes.Ldfld, fieldRef));
                worker.Append(worker.Create(OpCodes.Call, writeFunc));
            }

            if (fields == 0)
            {
                Log.Warning($"{variable} has no no public or non-static fields to serialize");
            }

            return true;
        }

        static MethodDefinition GenerateArrayWriteFunc(TypeReference variable, int recursionCount)
        {
            if (!variable.IsArrayType())
            {
                throw new GenerateWriterException($"{variable.Name} is an unsupported type. Jagged and multidimensional arrays are not supported", variable);
            }

            TypeReference elementType = variable.GetElementType();
            MethodReference elementWriteFunc = GetWriteFunc(elementType, recursionCount + 1);
            MethodReference intWriterFunc = GetWriteFunc(WeaverTypes.int32Type);

            string functionName = "_WriteArray" + variable.GetElementType().Name + "_";
            if (variable.DeclaringType != null)
            {
                functionName += variable.DeclaringType.Name;
            }
            else
            {
                functionName += "None";
            }

            // create new writer for this type
            MethodDefinition writerFunc = new MethodDefinition(functionName,
                    MethodAttributes.Public |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    WeaverTypes.voidType);

            writerFunc.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None, Weaver.CurrentAssembly.MainModule.ImportReference(WeaverTypes.NetworkWriterType)));
            writerFunc.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, Weaver.CurrentAssembly.MainModule.ImportReference(variable)));

            writerFunc.Body.Variables.Add(new VariableDefinition(WeaverTypes.int32Type));
            writerFunc.Body.Variables.Add(new VariableDefinition(WeaverTypes.int32Type));
            writerFunc.Body.InitLocals = true;

            ILProcessor worker = writerFunc.Body.GetILProcessor();

            // if (value == null)
            // {
            //     writer.WritePackedInt32(-1);
            //     return;
            // }
            Instruction labelNull = worker.Create(OpCodes.Nop);
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            worker.Append(worker.Create(OpCodes.Brtrue, labelNull));

            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldc_I4_M1));
            worker.Append(worker.Create(OpCodes.Call, intWriterFunc));
            worker.Append(worker.Create(OpCodes.Ret));

            // else not null
            worker.Append(labelNull);

            // int length = value.Length;
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            worker.Append(worker.Create(OpCodes.Ldlen));
            worker.Append(worker.Create(OpCodes.Stloc_0));

            // writer.WritePackedInt32(length);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldloc_0));
            worker.Append(worker.Create(OpCodes.Call, intWriterFunc));

            // for (int i=0; i< value.length; i++) {
            worker.Append(worker.Create(OpCodes.Ldc_I4_0));
            worker.Append(worker.Create(OpCodes.Stloc_1));
            Instruction labelHead = worker.Create(OpCodes.Nop);
            worker.Append(worker.Create(OpCodes.Br, labelHead));

            // loop body
            Instruction labelBody = worker.Create(OpCodes.Nop);
            worker.Append(labelBody);
            // writer.Write(value[i]);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldelema, elementType));
            worker.Append(worker.Create(OpCodes.Ldobj, elementType));
            worker.Append(worker.Create(OpCodes.Call, elementWriteFunc));

            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldc_I4_1));
            worker.Append(worker.Create(OpCodes.Add));
            worker.Append(worker.Create(OpCodes.Stloc_1));

            // end for loop
            worker.Append(labelHead);
            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            worker.Append(worker.Create(OpCodes.Ldlen));
            worker.Append(worker.Create(OpCodes.Conv_I4));
            worker.Append(worker.Create(OpCodes.Blt, labelBody));

            // return
            worker.Append(worker.Create(OpCodes.Ret));
            return writerFunc;
        }

        static MethodDefinition GenerateArraySegmentWriteFunc(TypeReference variable, int recursionCount)
        {
            GenericInstanceType genericInstance = (GenericInstanceType)variable;
            TypeReference elementType = genericInstance.GenericArguments[0];
            MethodReference elementWriteFunc = GetWriteFunc(elementType, recursionCount + 1);
            MethodReference intWriterFunc = GetWriteFunc(WeaverTypes.int32Type);

            string functionName = "_WriteArraySegment_" + elementType.Name + "_";
            if (variable.DeclaringType != null)
            {
                functionName += variable.DeclaringType.Name;
            }
            else
            {
                functionName += "None";
            }

            // create new writer for this type
            MethodDefinition writerFunc = new MethodDefinition(functionName,
                    MethodAttributes.Public |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    WeaverTypes.voidType);

            writerFunc.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None, Weaver.CurrentAssembly.MainModule.ImportReference(WeaverTypes.NetworkWriterType)));
            writerFunc.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, variable));

            writerFunc.Body.Variables.Add(new VariableDefinition(WeaverTypes.int32Type));
            writerFunc.Body.Variables.Add(new VariableDefinition(WeaverTypes.int32Type));
            writerFunc.Body.InitLocals = true;

            ILProcessor worker = writerFunc.Body.GetILProcessor();

            MethodReference countref = WeaverTypes.ArraySegmentCountReference.MakeHostInstanceGeneric(genericInstance);

            // int length = value.Count;
            worker.Append(worker.Create(OpCodes.Ldarga_S, (byte)1));
            worker.Append(worker.Create(OpCodes.Call, countref));
            worker.Append(worker.Create(OpCodes.Stloc_0));


            // writer.WritePackedInt32(length);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldloc_0));
            worker.Append(worker.Create(OpCodes.Call, intWriterFunc));

            // Loop through the ArraySegment<T> and call the writer for each element.
            // generates this:
            // for (int i=0; i< length; i++)
            // {
            //    writer.Write(value.Array[i + value.Offset]);
            // }
            worker.Append(worker.Create(OpCodes.Ldc_I4_0));
            worker.Append(worker.Create(OpCodes.Stloc_1));
            Instruction labelHead = worker.Create(OpCodes.Nop);
            worker.Append(worker.Create(OpCodes.Br, labelHead));

            // loop body
            Instruction labelBody = worker.Create(OpCodes.Nop);
            worker.Append(labelBody);

            // writer.Write(value.Array[i + value.Offset]);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldarga_S, (byte)1));
            worker.Append(worker.Create(OpCodes.Call, WeaverTypes.ArraySegmentArrayReference.MakeHostInstanceGeneric(genericInstance)));
            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldarga_S, (byte)1));
            worker.Append(worker.Create(OpCodes.Call, WeaverTypes.ArraySegmentOffsetReference.MakeHostInstanceGeneric(genericInstance)));
            worker.Append(worker.Create(OpCodes.Add));
            worker.Append(worker.Create(OpCodes.Ldelema, elementType));
            worker.Append(worker.Create(OpCodes.Ldobj, elementType));
            worker.Append(worker.Create(OpCodes.Call, elementWriteFunc));


            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldc_I4_1));
            worker.Append(worker.Create(OpCodes.Add));
            worker.Append(worker.Create(OpCodes.Stloc_1));

            // end for loop
            worker.Append(labelHead);
            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldloc_0));
            worker.Append(worker.Create(OpCodes.Blt, labelBody));

            // return
            worker.Append(worker.Create(OpCodes.Ret));
            return writerFunc;
        }

        static MethodDefinition GenerateListWriteFunc(TypeReference variable, int recursionCount)
        {
            GenericInstanceType genericInstance = (GenericInstanceType)variable;
            TypeReference elementType = genericInstance.GenericArguments[0];
            MethodReference elementWriteFunc = GetWriteFunc(elementType, recursionCount + 1);
            MethodReference intWriterFunc = GetWriteFunc(WeaverTypes.int32Type);


            string functionName = "_WriteList_" + elementType.Name + "_";
            if (variable.DeclaringType != null)
            {
                functionName += variable.DeclaringType.Name;
            }
            else
            {
                functionName += "None";
            }

            // create new writer for this type
            MethodDefinition writerFunc = new MethodDefinition(functionName,
                    MethodAttributes.Public |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    WeaverTypes.voidType);

            writerFunc.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None, Weaver.CurrentAssembly.MainModule.ImportReference(WeaverTypes.NetworkWriterType)));
            writerFunc.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, variable));

            writerFunc.Body.Variables.Add(new VariableDefinition(WeaverTypes.int32Type));
            writerFunc.Body.Variables.Add(new VariableDefinition(WeaverTypes.int32Type));
            writerFunc.Body.InitLocals = true;

            ILProcessor worker = writerFunc.Body.GetILProcessor();

            // if (value == null)
            // {
            //     writer.WritePackedInt32(-1);
            //     return;
            // }
            Instruction labelNull = worker.Create(OpCodes.Nop);
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            worker.Append(worker.Create(OpCodes.Brtrue, labelNull));

            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldc_I4_M1));
            worker.Append(worker.Create(OpCodes.Call, intWriterFunc));
            worker.Append(worker.Create(OpCodes.Ret));

            // else not null
            worker.Append(labelNull);

            MethodReference countref = WeaverTypes.ListCountReference.MakeHostInstanceGeneric(genericInstance);

            // int count = value.Count;
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            worker.Append(worker.Create(OpCodes.Call, countref));
            worker.Append(worker.Create(OpCodes.Stloc_0));

            // writer.WritePackedInt32(count);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldloc_0));
            worker.Append(worker.Create(OpCodes.Call, intWriterFunc));

            // Loop through the List<T> and call the writer for each element.
            // generates this:
            // for (int i=0; i < count; i++)
            // {
            //    writer.WriteT(value[i]);
            // }
            worker.Append(worker.Create(OpCodes.Ldc_I4_0));
            worker.Append(worker.Create(OpCodes.Stloc_1));
            Instruction labelHead = worker.Create(OpCodes.Nop);
            worker.Append(worker.Create(OpCodes.Br, labelHead));

            // loop body
            Instruction labelBody = worker.Create(OpCodes.Nop);
            worker.Append(labelBody);

            MethodReference getItem = WeaverTypes.ListGetItemReference.MakeHostInstanceGeneric(genericInstance);

            // writer.Write(value[i]);
            worker.Append(worker.Create(OpCodes.Ldarg_0)); // writer
            worker.Append(worker.Create(OpCodes.Ldarg_1)); // value
            worker.Append(worker.Create(OpCodes.Ldloc_1)); // i
            worker.Append(worker.Create(OpCodes.Call, getItem)); //get_Item
            worker.Append(worker.Create(OpCodes.Call, elementWriteFunc)); // Write


            // end for loop

            // for loop i++ 
            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldc_I4_1));
            worker.Append(worker.Create(OpCodes.Add));
            worker.Append(worker.Create(OpCodes.Stloc_1));

            worker.Append(labelHead);
            // for loop i < count
            worker.Append(worker.Create(OpCodes.Ldloc_1));
            worker.Append(worker.Create(OpCodes.Ldloc_0));
            worker.Append(worker.Create(OpCodes.Blt, labelBody));

            // return
            worker.Append(worker.Create(OpCodes.Ret));
            return writerFunc;
        }
    }
}
