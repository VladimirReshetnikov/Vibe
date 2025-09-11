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
        if (staticType is null)
            throw new ArgumentNullException(nameof(staticType));
        return new StaticTypeProxy(staticType);
    }

    private sealed class StaticTypeProxy : DynamicObject
    {
        private readonly Type _type;

        public StaticTypeProxy(Type type)
        {
            _type = type;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
        {
            result = null;
            var argInfo = new List<CSharpArgumentInfo>
            {
                CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsStaticType, null)
            };
            var argExpr = new List<Expression>
            {
                Expression.Constant(_type, typeof(object))
            };
            if (args is not null)
            {
                foreach (var arg in args)
                {
                    argInfo.Add(CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null));
                    argExpr.Add(Expression.Constant(arg, typeof(object)));
                }
            }

            var isVoid = IsVoidMethod(binder.Name, args);
            var flags = isVoid ? CSharpBinderFlags.ResultDiscarded : CSharpBinderFlags.None;
            var invokeBinder = CSharpBinder.InvokeMember(
                flags,
                binder.Name,
                null,
                typeof(StaticTypeProxy),
                argInfo);

            if (isVoid)
            {
                var expr = Expression.Dynamic(invokeBinder, typeof(void), argExpr);
                Expression.Lambda<Action>(expr).Compile().Invoke();
                result = null;
            }
            else
            {
                var expr = Expression.Dynamic(invokeBinder, typeof(object), argExpr);
                result = Expression.Lambda<Func<object?>>(expr).Compile().Invoke();
            }
            return true;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            var argInfo = new[]
            {
                CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsStaticType, null)
            };
            var getBinder = CSharpBinder.GetMember(CSharpBinderFlags.None, binder.Name, typeof(StaticTypeProxy), argInfo);
            var expr = Expression.Dynamic(getBinder, typeof(object), Expression.Constant(_type, typeof(object)));
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
                Expression.Constant(_type, typeof(object)),
                Expression.Constant(value, typeof(object))
            };
            var setBinder = CSharpBinder.SetMember(CSharpBinderFlags.None, binder.Name, typeof(StaticTypeProxy), argInfo);
            var expr = Expression.Dynamic(setBinder, typeof(object), argExpr);
            Expression.Lambda<Action>(expr).Compile().Invoke();
            return true;
        }

        private bool IsVoidMethod(string name, object?[]? args)
        {
            var argTypes = args is null
                ? Type.EmptyTypes
                : args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
            var method = _type.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy,
                Type.DefaultBinder,
                argTypes,
                null);
            return method?.ReturnType == typeof(void);
        }
    }
}

