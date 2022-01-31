using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace MinimalApis.Extensions.Binding;

/// <summary>
/// Represents a type that will have its properties bound as if they were parameters of a route handler.
/// </summary>
public interface IBindProperties
{
    /// <summary>
    /// Returns the bound value for the specified <paramref name="parameter"/>.
    /// </summary>
    /// <param name="context">The <see cref="HttpContext"/> for the current request.</param>
    /// <param name="parameter">The <see cref="ParameterInfo"/> for the parameter to bind a value for.</param>
    /// <returns>The value to populate the target parameter with.</returns>
    public static ValueTask<TValue> BindAsync<TValue>(HttpContext context, ParameterInfo parameter)
        where TValue : new()
    {


        return ValueTask.FromResult(new TValue());
    }
}
