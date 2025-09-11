// SPDX-License-Identifier: MIT-0

using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.CSharp.RuntimeBinder;
using CSharpBinder = Microsoft.CSharp.RuntimeBinder.Binder;

namespace Vibe.Utils;

public static class TypeExtensions
{
    /// <summary>
    /// Creates a dynamic object exposing the static members of the provided <see cref="Type"/>.
    /// </summary>
    /// <param name="staticType">Type whose static members should be exposed.</param>
    /// <returns>A dynamic object mirroring the static members of <paramref name="staticType"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="staticType"/> is <c>null</c>.</exception>
    public static dynamic ToDynamicObject(this Type staticType)
    {
        ArgumentNullException.ThrowIfNull(staticType);
        return new StaticTypeProxy(staticType);
    }

    private sealed class StaticTypeProxy(Type type) : DynamicObject
    {
        public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
        {
            result = null;
            var argInfo = new List<CSharpArgumentInfo>
            {
                CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsStaticType, null)
            };
            var argExpr = new List<Expression>
            {
                Expression.Constant(type, typeof(object))
            };
            if (args is not null)
            {
                foreach (var arg in args)
                {
                    argInfo.Add(CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null));
                    argExpr.Add(Expression.Constant(arg, typeof(object)));
                }
            }

            var invokeBinder = CSharpBinder.InvokeMember(
                CSharpBinderFlags.None,
                binder.Name,
                null,
                typeof(StaticTypeProxy),
                argInfo);

            var expr = Expression.Dynamic(invokeBinder, typeof(object), argExpr);
            var lambda = Expression.Lambda<Func<object?>>(expr);
            result = lambda.Compile().Invoke();
            return true;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            // If the requested member is a public nested static type, return its proxy directly.
            var nestedType = type.GetNestedType(binder.Name, BindingFlags.Public);
            if (nestedType is not null && nestedType.IsAbstract && nestedType.IsSealed)
            {
                result = new StaticTypeProxy(nestedType);
                return true;
            }

            var argInfo = new[]
            {
                CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsStaticType, null)
            };
            var getBinder = CSharpBinder.GetMember(CSharpBinderFlags.None, binder.Name, typeof(StaticTypeProxy), argInfo);
            var expr = Expression.Dynamic(getBinder, typeof(object), Expression.Constant(type, typeof(object)));
            result = Expression.Lambda<Func<object?>>(expr).Compile().Invoke();
            return true;
        }

        public override bool TrySetMember(SetMemberBinder binder, object? value)
        {
            var argInfo = new[]
            {
                CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsStaticType, null),
                CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null)
            };
            var argExpr = new Expression[]
            {
                Expression.Constant(type, typeof(object)),
                Expression.Constant(value, typeof(object))
            };
            var setBinder = CSharpBinder.SetMember(CSharpBinderFlags.None, binder.Name, typeof(StaticTypeProxy), argInfo);
            var expr = Expression.Dynamic(setBinder, typeof(object), argExpr);
            Expression.Lambda<Action>(expr).Compile().Invoke();
            return true;
        }
    }
}

