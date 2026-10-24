using System.Reflection;

namespace Specrig;

public class Proxy : DispatchProxy
{
    Func<MethodInfo, object?[], object?> handle = (method, _) => throw new InvalidOperationException($"{method.Name}: a proxy nobody wired");

    internal static T Of<T>(Func<MethodInfo, object?[], object?> handle) where T : class => (T)Of(typeof(T), handle);

    internal static object Of(Type type, Func<MethodInfo, object?[], object?> handle)
    {
        var proxy = Create(type, typeof(Proxy));
        ((Proxy)proxy).handle = handle;
        return proxy;
    }

    // A call of the library fails while the fault holds; a factory of it does not fail but
    // hands back a proxy too, so the database a service took before the fault fails with it.
    internal static object Failing(Type type, object target, Func<Exception?> fault) => Of(type, (method, args) =>
    {
        var library = method.DeclaringType?.Namespace == type.Namespace;
        var factory = method.ReturnType.IsInterface && method.ReturnType.Namespace == type.Namespace;
        if (library && !factory && !method.IsSpecialName && fault() is { } error)
        {
            throw error;
        }
        var result = method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);
        return result is not null && factory ? Failing(method.ReturnType, result, fault) : result;
    });

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handle(targetMethod!, args ?? []);

    internal static object? Done(Type type, object? value = null)
    {
        if (type == typeof(void))
        {
            return null;
        }
        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }
        if (type == typeof(ValueTask))
        {
            return default(ValueTask);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value ?? Default(result)]);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var result = type.GetGenericArguments()[0];
            return typeof(ValueTask).GetMethod(nameof(ValueTask.FromResult))!.MakeGenericMethod(result).Invoke(null, [value ?? Default(result)]);
        }
        return value ?? Default(type);
    }

    static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
