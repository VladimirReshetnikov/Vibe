namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Simplifies arithmetic expressions by applying identity rules
/// such as x + 0 => x or x * 1 => x.
/// </summary>
public sealed class SimplifyArithmeticPass : IRRewriter, ITransformationPass
{
    public void Run(IR.FunctionIR fn)
    {
        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
            {
                bb.Statements[i] = RewriteStmt(bb.Statements[i]);
            }
        }
    }

    protected override IR.Expr RewriteBinOp(IR.BinOpExpr b)
    {
        var L = RewriteExpr(b.Left);
        var R = RewriteExpr(b.Right);

        switch (b.Op)
        {
            case IR.BinOp.Add:
                if (IsZero(L)) return R;
                if (IsZero(R)) return L;
                break;
            case IR.BinOp.Sub:
                if (IsZero(R)) return L;
                if (ExpressionsEqual(L, R)) return MakeZeroFrom(L, R);
                break;
            case IR.BinOp.Mul:
                if (IsZero(L) || IsZero(R)) return MakeZeroFrom(L, R);
                if (IsOne(L)) return R;
                if (IsOne(R)) return L;
                break;
            case IR.BinOp.UDiv:
            case IR.BinOp.SDiv:
                if (IsOne(R)) return L;
                break;
            case IR.BinOp.And:
                if (IsZero(L) || IsZero(R)) return MakeZeroFrom(L, R);
                if (IsAllOnes(L)) return R;
                if (IsAllOnes(R)) return L;
                if (ExpressionsEqual(L, R)) return L;
                break;
            case IR.BinOp.Or:
                if (IsZero(L)) return R;
                if (IsZero(R)) return L;
                if (ExpressionsEqual(L, R)) return L;
                if (IsAllOnes(L)) return L;
                if (IsAllOnes(R)) return R;
                break;
            case IR.BinOp.Xor:
                if (IsZero(L)) return R;
                if (IsZero(R)) return L;
                if (ExpressionsEqual(L, R)) return MakeZeroFrom(L, R);
                break;
            case IR.BinOp.Shl:
            case IR.BinOp.Shr:
            case IR.BinOp.Sar:
                if (IsZero(R)) return L;
                break;
        }

        return new IR.BinOpExpr(b.Op, L, R);
    }

    private static bool IsZero(IR.Expr x) => x is IR.Const c && c.Value == 0 || x is IR.UConst uc && uc.Value == 0;
    private static bool IsOne(IR.Expr x) => x is IR.Const c && c.Value == 1 || x is IR.UConst uc && uc.Value == 1;
    private static bool IsAllOnes(IR.Expr x)
    {
        if (x is IR.Const c) return c.Value == -1;
        if (x is IR.UConst uc)
        {
            if (uc.Bits >= 64) return uc.Value == ulong.MaxValue;
            if (uc.Bits == 0) return false; // Edge case: avoid shift by 64
            return uc.Value == ((1UL << (int)uc.Bits) - 1);
        }
        return false;
    }
    // Relies on record-generated structural equality for expression types.
    private static bool ExpressionsEqual(IR.Expr a, IR.Expr b) => a.Equals(b);
    private static IR.Expr MakeZeroFrom(IR.Expr a, IR.Expr b)
    {
        int bits = GetBits(a) ?? GetBits(b) ?? 32;
        return new IR.Const(0, bits);
    }
    private static int? GetBits(IR.Expr e) => e switch
    {
        IR.Const c => c.Bits,
        IR.UConst uc => (int)uc.Bits,
        _ => null,
    };
}
