namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Evaluates constant expressions and replaces them with their folded result.
/// </summary>
public sealed class FoldConstantsPass : IRRewriter, ITransformationPass
{
    public void Run(IR.FunctionIR fn)
    {
        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
                bb.Statements[i] = RewriteStmt(bb.Statements[i]);
        }
    }

    protected override IR.Expr RewriteBinOp(IR.BinOpExpr b)
    {
        var l = RewriteExpr(b.Left);
        var r = RewriteExpr(b.Right);

        if (l is IR.Const lc && r is IR.Const rc)
        {
            return new IR.Const(EvalBinOpSigned(b.Op, lc.Value, rc.Value, Math.Max(lc.Bits, rc.Bits)), Math.Max(lc.Bits, rc.Bits));
        }
        if (l is IR.UConst lu && r is IR.UConst ru)
        {
            return new IR.UConst(EvalBinOpUnsigned(b.Op, lu.Value, ru.Value, Math.Max(lu.Bits, ru.Bits)), Math.Max(lu.Bits, ru.Bits));
        }
        return new IR.BinOpExpr(b.Op, l, r);
    }

    protected override IR.Expr RewriteUnOp(IR.UnOpExpr u)
    {
        var operand = RewriteExpr(u.Operand);
        if (operand is IR.Const c)
        {
            return u.Op switch
            {
                IR.UnOp.Neg => new IR.Const(FitSigned(-c.Value, c.Bits), c.Bits),
                IR.UnOp.Not => new IR.Const(FitSigned(~c.Value, c.Bits), c.Bits),
                IR.UnOp.LNot => new IR.Const(c.Value == 0 ? 1 : 0, 1),
                _ => new IR.UnOpExpr(u.Op, operand)
            };
        }
        if (operand is IR.UConst uc)
        {
            return u.Op switch
            {
                IR.UnOp.Neg => new IR.UConst(FitUnsigned((ulong)(-(long)uc.Value), uc.Bits), uc.Bits),
                IR.UnOp.Not => new IR.UConst(FitUnsigned(~uc.Value, uc.Bits), uc.Bits),
                IR.UnOp.LNot => new IR.Const(uc.Value == 0 ? 1 : 0, 1),
                _ => new IR.UnOpExpr(u.Op, operand)
            };
        }
        return new IR.UnOpExpr(u.Op, operand);
    }

    protected override IR.Expr RewriteCompare(IR.CompareExpr c)
    {
        var l = RewriteExpr(c.Left);
        var r = RewriteExpr(c.Right);

        if (l is IR.Const lc && r is IR.Const rc)
        {
            bool res = EvalComparison(c.Op, lc.Value, rc.Value);
            return new IR.Const(res ? 1 : 0, 1);
        }
        if (l is IR.UConst lu && r is IR.UConst ru)
        {
            bool res = EvalComparisonUnsigned(c.Op, lu.Value, ru.Value, (int)lu.Bits, (int)ru.Bits);
            return new IR.Const(res ? 1 : 0, 1);
        }
        return new IR.CompareExpr(c.Op, l, r);
    }

    private static long EvalBinOpSigned(IR.BinOp op, long l, long r, int bits)
    {
        long res = op switch
        {
            IR.BinOp.Add => l + r,
            IR.BinOp.Sub => l - r,
            IR.BinOp.Mul => l * r,
            IR.BinOp.SDiv => r == 0 ? l : l / r,
            IR.BinOp.SRem => r == 0 ? l : l % r,
            IR.BinOp.UDiv => r == 0 ? l : (long)((ulong)l / (ulong)r),
            IR.BinOp.URem => r == 0 ? l : (long)((ulong)l % (ulong)r),
            IR.BinOp.And => l & r,
            IR.BinOp.Or => l | r,
            IR.BinOp.Xor => l ^ r,
            IR.BinOp.Shl => (int)r >= bits ? 0 : l << (int)r,
            IR.BinOp.Shr => (int)r >= bits ? 0 : (long)((ulong)l >> (int)r),
            IR.BinOp.Sar => (int)r >= bits ? (l < 0 ? -1 : 0) : l >> (int)r,
            _ => l,
        };
        return FitSigned(res, bits);
    }

    private static ulong EvalBinOpUnsigned(IR.BinOp op, ulong l, ulong r, int bits)
    {
        ulong res = op switch
        {
            IR.BinOp.Add => l + r,
            IR.BinOp.Sub => l - r,
            IR.BinOp.Mul => l * r,
            IR.BinOp.UDiv => r == 0 ? l : l / r,
            IR.BinOp.URem => r == 0 ? l : l % r,
            IR.BinOp.And => l & r,
            IR.BinOp.Or => l | r,
            IR.BinOp.Xor => l ^ r,
            IR.BinOp.Shl => (int)r >= bits ? 0 : l << (int)r,
            IR.BinOp.Shr => (int)r >= bits ? 0 : l >> (int)r,
            IR.BinOp.Sar => (int)r >= bits ? (((long)l < 0) ? ulong.MaxValue : 0) : (ulong)((long)l >> (int)r),
            IR.BinOp.SDiv => r == 0 ? l : (ulong)((long)l / (long)r),
            IR.BinOp.SRem => r == 0 ? l : (ulong)((long)l % (long)r),
            _ => l,
        };
        return FitUnsigned(res, bits);
    }

    private static bool EvalComparison(IR.CmpOp op, long l, long r) => op switch
    {
        IR.CmpOp.EQ => l == r,
        IR.CmpOp.NE => l != r,
        IR.CmpOp.SLT => l < r,
        IR.CmpOp.SLE => l <= r,
        IR.CmpOp.SGT => l > r,
        IR.CmpOp.SGE => l >= r,
        IR.CmpOp.ULT => (ulong)l < (ulong)r,
        IR.CmpOp.ULE => (ulong)l <= (ulong)r,
        IR.CmpOp.UGT => (ulong)l > (ulong)r,
        IR.CmpOp.UGE => (ulong)l >= (ulong)r,
        _ => false,
    };

    private static bool EvalComparisonUnsigned(IR.CmpOp op, ulong l, ulong r, int lBits, int rBits) => op switch
    {
        IR.CmpOp.EQ => l == r,
        IR.CmpOp.NE => l != r,
        IR.CmpOp.ULT => l < r,
        IR.CmpOp.ULE => l <= r,
        IR.CmpOp.UGT => l > r,
        IR.CmpOp.UGE => l >= r,
        IR.CmpOp.SLT => SignExtend(l, lBits) < SignExtend(r, rBits),
        IR.CmpOp.SLE => SignExtend(l, lBits) <= SignExtend(r, rBits),
        IR.CmpOp.SGT => SignExtend(l, lBits) > SignExtend(r, rBits),
        IR.CmpOp.SGE => SignExtend(l, lBits) >= SignExtend(r, rBits),
        _ => false,
    };

    private static long SignExtend(ulong value, int bits)
    {
        if (bits >= 64) return (long)value;
        ulong mask = (1UL << bits) - 1;
        value &= mask;
        ulong sign = 1UL << (bits - 1);
        return (long)((value ^ sign) - sign);
    }

    private static long FitSigned(long value, int bits)
    {
        if (bits >= 64) return value;
        long mask = (1L << bits) - 1;
        value &= mask;
        long sign = 1L << (bits - 1);
        return (value ^ sign) - sign;
    }

    private static ulong FitUnsigned(ulong value, int bits)
    {
        if (bits >= 64) return value;
        ulong mask = (1UL << bits) - 1;
        return value & mask;
    }
}
