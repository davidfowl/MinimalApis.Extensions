using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace MinimalApis.Extensions.Binding;

internal static class CodeGenerator
{
    private static readonly MethodInfo CompletedTask = typeof(Task).GetMethod("get_CompletedTask")!;
    private static readonly MethodInfo HttpContext_getItems = typeof(HttpContext).GetMethod("get_Items")!;
    private static readonly MethodInfo Dictionary_setItem = typeof(IDictionary<object, object?>).GetMethod("set_Item")!;
    private static readonly Type[] IResult_types = new[] { typeof(IResult) };
    private static readonly Type[] ExecuteAsync_ParamTypes = new[] { typeof(HttpContext) };
    private static readonly MethodInfo IResult_ExecuteAsync = typeof(IResult).GetMethod("ExecuteAsync")!;
    //private static readonly Type[] Execute_ParamTypes = new[] { typeof(TValue), typeof(HttpContext) };
    //private static readonly Type RouteHandler_DelegateType = typeof(Func<,,>).MakeGenericType(typeof(TValue), typeof(HttpContext), typeof(IResult));

    
    private static readonly ConcurrentDictionary<(Type, ParameterInfo?), RequestDelegate> _delegateCache = new();

    public static readonly string BindOnlyItemsKey = $"__{nameof(CreateBindOnlyRequestDelegate)}_ValueResult_Key";

    public static RequestDelegate GetBindOnlyRequestDelegate<TValue>(ParameterInfo? parameter)
    {
        var cacheKey = (typeof(TValue), parameter);

        var requestDelegate = _delegateCache.GetOrAdd(cacheKey, CreateBindOnlyRequestDelegate<TValue>);

        return requestDelegate;
    }
    
    // We're using RefEmit instead of Expression<T> here so that we can preserve the original parameter name. It seems
    // that RefEmit is the only runtime code generation technique that supports this right now. Could explore using a
    // source generator instead perhaps to see if it's a good fit plus it might be the only way to preserve parameter
    // attributes (which this doesn't do right now).
    private static RequestDelegate CreateBindOnlyRequestDelegate<TValue>((Type targetType, ParameterInfo? parameter) key)
    {
        // Route handler delegate to generate:
        // public static IResult Execute(TValueType originalParameterName, HttpContext httpContext)
        // {
        //     httpContext.Items["ValueOf_itemsKey"] = originalParameterName;
        //     return _result;
        // }

        var routeHandlerDelegateType = typeof(Func<,,>).MakeGenericType(typeof(TValue), typeof(HttpContext), typeof(IResult));

        var parameter = key.parameter;
        var parameterName = parameter?.Name ?? "value";

        var routeHandlerDelegate = RouteHandlerGenerator<TValue>.Create(parameter, routeHandlerDelegateType, (handlerMethod, resultField) =>
        {
            // Method parameters start at 1, 0 is the return value
            handlerMethod.DefineParameter(1, ParameterAttributes.None, parameterName);
            handlerMethod.DefineParameter(2, ParameterAttributes.None, "httpContext");

            // TODO: Clone attributes from original parameter on to generated parameter
            //       This might be tricky as we don't have the source of the original parameter, only the instances which we
            //       we need to use to reverse-engineer IL source that would produce that instance
            //param1.SetCustomAttribute(new CustomAttributeBuilder())
            //var param1attr = parameter.GetCustomAttributes(false);
            //for (int i = 0; i < param1attr.Length; i++)
            //{
            //    var attr = param1attr[i];
            //}

            var handlerMethodBody = handlerMethod.GetILGenerator();
            // httpContext.Items["MyItemsKey"] = value;
            handlerMethodBody.Emit(OpCodes.Ldarg_1);
            handlerMethodBody.EmitCall(OpCodes.Callvirt, HttpContext_getItems, null);
            handlerMethodBody.Emit(OpCodes.Ldstr, BindOnlyItemsKey);
            handlerMethodBody.Emit(OpCodes.Ldarg_0);
            handlerMethodBody.EmitCall(OpCodes.Callvirt, Dictionary_setItem, null);

            // return _result;
            handlerMethodBody.Emit(OpCodes.Ldsfld, resultField);
            handlerMethodBody.Emit(OpCodes.Ret);
        });

        return RequestDelegateFactory.Create(routeHandlerDelegate).RequestDelegate;
    }

    private static class RouteHandlerGenerator<THandler> where THandler : delegate
    {
        //private static readonly Type[] Execute_ParamTypes = new[] { typeof(TValue), typeof(HttpContext) };

        public static Delegate Create(ParameterInfo? parameter, Type routeHandlerDelegateType, Action<MethodBuilder, FieldBuilder> generateExecuteMethod)
        {
            // Module to generate:
            // class FakeResult : IResult
            // {
            //     public Task ExecuteAsync(HttpContext httpContext)
            //     {
            //         return Task.CompletedTask;
            //     }
            // }
            // public static class RouteHandler
            // {
            //     private static readonly IResult _result = new FakeResult();
            //
            //     public static IResult Execute(TValueType originalParameterName, HttpContext httpContext)
            //     {
            //         // Generated execute body
            //         return _result;
            //     }
            // }

            var parameterName = parameter?.Name ?? "value";

            var assemblyName = $"{nameof(GetBindOnlyRequestDelegate)}.{typeof(TValue).Name}.Assembly.{typeof(TValue).Name}.{parameterName}";
            var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
            var module = asm.DefineDynamicModule(assemblyName);
            var fakeResultTypeCtor = GenerateFakeResultType(module);

            // public sealed class RouteHandler {
            var routeHandlerBuilder = module.DefineType("RouteHandler", TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed);

            // private static IResult _result = new FakeResult();
            var resultInstanceField = routeHandlerBuilder.DefineField("_result", typeof(IResult), FieldAttributes.Private | FieldAttributes.Static);
            var routeHandlerStaticCtor = routeHandlerBuilder.DefineConstructor(MethodAttributes.Private | MethodAttributes.Static, CallingConventions.Standard, null);
            var routeHandlerStaticCtorIl = routeHandlerStaticCtor.GetILGenerator();
            routeHandlerStaticCtorIl.Emit(OpCodes.Newobj, fakeResultTypeCtor);
            routeHandlerStaticCtorIl.Emit(OpCodes.Stsfld, resultInstanceField);
            routeHandlerStaticCtorIl.Emit(OpCodes.Ret);

            // public static IResult Execute(MyType value, HttpContext httpContext) {
            var routeHandlerExecute = routeHandlerBuilder.DefineMethod("Execute", MethodAttributes.Public | MethodAttributes.Static, typeof(IResult), Execute_ParamTypes);

            generateExecuteMethod(routeHandlerExecute, resultInstanceField);

            var routeHandlerType = routeHandlerBuilder.CreateType()!;

            // Create the route handler delegate
            // Func<TargetType, HttpContext, IResult>
            var routeHandlerMethod = routeHandlerType.GetMethod("Execute")!;
            var routeHandlerDelegate = routeHandlerMethod.CreateDelegate(routeHandlerDelegateType);

            return routeHandlerDelegate;
        }

        private static ConstructorInfo GenerateFakeResultType(ModuleBuilder module)
        {
            // internal sealed class FakeResult : IResult {
            var fakeResultBuilder = module.DefineType("FakeResult", TypeAttributes.Class | TypeAttributes.Sealed, null, IResult_types);
            fakeResultBuilder.AddInterfaceImplementation(typeof(IResult));

            // public Task ExecuteAsync(HttpContext context) {
            var fakeResultExecuteAsync = fakeResultBuilder.DefineMethod("ExecuteAsync", MethodAttributes.Public | MethodAttributes.Virtual, typeof(Task), ExecuteAsync_ParamTypes);
            fakeResultExecuteAsync.DefineParameter(0, ParameterAttributes.None, "httpContext");
            fakeResultBuilder.DefineMethodOverride(fakeResultExecuteAsync, IResult_ExecuteAsync);
            var fakeResultExecuteAsyncIl = fakeResultExecuteAsync.GetILGenerator();
            fakeResultExecuteAsyncIl.EmitCall(OpCodes.Call, CompletedTask, null);
            fakeResultExecuteAsyncIl.Emit(OpCodes.Ret);

            Type fakeResultType = fakeResultBuilder.CreateType()!;
            var fakeResultTypeCtor = fakeResultType.GetConstructor(Type.EmptyTypes)!;
            return fakeResultTypeCtor;
        }
    }

    public static RequestDelegate CreateParametersObjectRequestDelegate<TValue>()
    {
        // Module to generate:
        // class FakeResult : IResult
        // {
        //     public Task ExecuteAsync(HttpContext httpContext)
        //     {
        //         return Task.CompletedTask;
        //     }
        // }
        // public static class RouteHandler
        // {
        //     private static readonly IResult _result = new FakeResult();
        //
        //     public static IResult Execute(Type1 prop1, Type2 prop2, Type3 prop3, HttpContext httpContext)
        //     {
        //         var boundObject = new BoundObjectType();
        //         boundObject.Prop1 = prop1;
        //         boundObject.Prop2 = prop2;
        //         boundObject.Prop3 = prop3;
        //         httpContext.Items["ValueOf_itemsKey"] = boundObject;
        //         return _result;
        //     }
        // }

        // Generates a delegate with a parameter per property on the type including prop->param attributes
        return null;
    }
}
