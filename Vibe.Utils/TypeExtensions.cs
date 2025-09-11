// SPDX-License-Identifier: MIT-0

using System.Dynamic;
using System.Linq.Expressions;
using Microsoft.CSharp.RuntimeBinder;

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
            
            try
            {
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

                var invokeBinder = Binder.InvokeMember(
                    CSharpBinderFlags.None,
                    binder.Name,
                    null,
                    typeof(StaticTypeProxy),
                    argInfo);

                // Create a wrapper that handles both void and non-void methods
                var callExpression = Expression.Dynamic(invokeBinder, typeof(object), argExpr);
                var wrapperExpression = Expression.Block(
                    typeof(object),
                    Expression.TryCatch(
                        callExpression,
                        Expression.Catch(
                            typeof(RuntimeBinderException),
                            Expression.Block(
                                // If the first attempt fails, try as void method
                                Expression.Dynamic(
                                    Binder.InvokeMember(
                                        CSharpBinderFlags.ResultDiscarded,
                                        binder.Name,
                                        null,
                                        typeof(StaticTypeProxy),
                                        argInfo),
                                    typeof(void),
                                    argExpr),
                                Expression.Constant(null, typeof(object))
                            )
                        )
                    )
                );

                var lambda = Expression.Lambda<Func<object?>>(wrapperExpression);
                result = lambda.Compile().Invoke();
                return true;
            }
            catch (RuntimeBinderException)
            {
                return false;
            }
        }

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            try
            {
                var argInfo = new[]
                {
                    CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsStaticType, null)
                };
                var getBinder = Binder.GetMember(CSharpBinderFlags.None, binder.Name, typeof(StaticTypeProxy), argInfo);
                var expr = Expression.Dynamic(getBinder, typeof(object), Expression.Constant(_type, typeof(object)));
                result = Expression.Lambda<Func<object?>>(expr).Compile().Invoke();
                return true;
            }
            catch (RuntimeBinderException)
            {
                result = null;
                return false;
            }
        }

        public override bool TrySetMember(SetMemberBinder binder, object? value)
        {
            try
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
                var setBinder = Binder.SetMember(CSharpBinderFlags.None, binder.Name, typeof(StaticTypeProxy), argInfo);
                var expr = Expression.Dynamic(setBinder, typeof(object), argExpr);
                Expression.Lambda<Action>(expr).Compile().Invoke();
                return true;
            }
            catch (RuntimeBinderException)
            {
                return false;
            }
        }
    }
}

