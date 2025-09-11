// SPDX-License-Identifier: MIT-0

using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            var variables = new List<ParameterExpression>();
            var preAssign = new List<Expression>();
            var postAssign = new List<Expression>();

            if (args is not null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (arg is IStrongBox)
                    {
                        var boxType = arg.GetType();
                        var valueType = boxType.GetProperty(nameof(IStrongBox.Value))!.PropertyType;

                        var temp = Expression.Variable(valueType, $"arg{i}");
                        variables.Add(temp);

                        var boxExpr = Expression.Constant(arg, boxType);
                        var valueExpr = Expression.Property(boxExpr, nameof(IStrongBox.Value));

                        preAssign.Add(Expression.Assign(temp, valueExpr));
                        postAssign.Add(Expression.Assign(valueExpr, temp));

                        // Treat IStrongBox arguments as by-ref at the binder level.
                        argInfo.Add(CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.IsRef, null));
                        argExpr.Add(temp);
                    }
                    else
                    {
                        argInfo.Add(CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null));
                        argExpr.Add(Expression.Constant(arg, typeof(object)));
                    }
                }
            }

            var isVoid = IsVoidMethod(binder.Name, args);
            var flags = isVoid ? CSharpBinderFlags.ResultDiscarded : CSharpBinderFlags.None;

            var invokeBinder = CSharpBinder.InvokeMember(
                flags,
                binder.Name,
                typeArguments: null,
                context: typeof(StaticTypeProxy),
                argumentInfo: argInfo);

            // Build the core dynamic call expression (object when value is needed, void when discarded).
            Expression call = Expression.Dynamic(invokeBinder, isVoid ? typeof(void) : typeof(object), argExpr);

            // If we have by-ref boxes, wrap the call in a block that handles pre/post assignments.
            if (variables.Count > 0)
            {
                var block = new List<Expression>();
                block.AddRange(preAssign);

                if (isVoid)
                {
                    // call; postAssign; (ensure block type is void)
                    block.Add(call);
                    block.AddRange(postAssign);
                    block.Add(Expression.Empty());
                    call = Expression.Block(variables, block);
                }
                else
                {
                    // var result; result = call; postAssign; return result;
                    var resultVar = Expression.Variable(typeof(object), "result");
                    variables.Add(resultVar);

                    block.Add(Expression.Assign(resultVar, call));
                    block.AddRange(postAssign);
                    block.Add(resultVar);

                    call = Expression.Block(variables, block);
                }
            }

            if (isVoid)
            {
                Expression.Lambda<Action>(call).Compile().Invoke();
                result = null;
            }
            else
            {
                result = Expression.Lambda<Func<object?>>(call).Compile().Invoke();
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

            var candidates = _type
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                .Where(m => m.Name == name)
                .Cast<MethodBase>()
                .ToArray();

            var method = (MethodInfo?)Type.DefaultBinder.SelectMethod(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy,
                candidates,
                argTypes,
                null);

            return method?.ReturnType == typeof(void);
        }
    }
}

