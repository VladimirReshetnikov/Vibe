// SPDX-License-Identifier: MIT-0
public static class Transformations
{
    // --- Helper rewriter ---
    private static IR.Stmt RewriteStmt(IR.Stmt s, Func<IR.Expr, IR.Expr> f)
    {
        switch (s)
        {
            case IR.AssignStmt a: return new IR.AssignStmt(f(a.Lhs), f(a.Rhs));
            case IR.StoreStmt st: return new IR.StoreStmt(f(st.Address), f(st.Value), st.ElemType, st.Segment);
            case IR.CallStmt cs:  return new IR.CallStmt(new IR.CallExpr(cs.Call.Target, cs.Call.Args.Select(f).ToList()));
            case IR.IfGotoStmt ig: return new IR.IfGotoStmt(f(ig.Condition), ig.Target);
            case IR.ReturnStmt r: return r.Value is null ? r : new IR.ReturnStmt(f(r.Value));
            default: return s;
        }
    }

    private static IR.Expr RewriteExpr(IR.Expr e, Func<IR.Expr, IR.Expr> f)
    {
        IR.Expr Recurse(IR.Expr x) => RewriteExpr(x, f);

        switch (e)
        {
            case IR.BinOpExpr b: return f(new IR.BinOpExpr(b.Op, Recurse(b.Left), Recurse(b.Right)));
            case IR.UnOpExpr u:  return f(new IR.UnOpExpr(u.Op, Recurse(u.Operand)));
            case IR.CompareExpr c: return f(new IR.CompareExpr(c.Op, Recurse(c.Left), Recurse(c.Right)));
            case IR.TernaryExpr t: return f(new IR.TernaryExpr(Recurse(t.Condition), Recurse(t.WhenTrue), Recurse(t.WhenFalse)));
            case IR.CastExpr ce: return f(new IR.CastExpr(Recurse(ce.Value), ce.TargetType, ce.Kind));
            case IR.CallExpr call: return f(new IR.CallExpr(call.Target, call.Args.Select(Recurse).ToList()));
            case IR.LoadExpr ld: return f(new IR.LoadExpr(Recurse(ld.Address), ld.ElemType, ld.Segment));
            default: return f(e);
        }
    }

    // ----------------------------------------------------------------
    //  No-op placeholder: keep param names stable (RCX..R9 => p1..p4)
    // ----------------------------------------------------------------
    public static void ReplaceParamRegsWithParams(IR.FunctionIR fn)
    {
        // Our current emitter already names RCX..R9 as p1..p4; nothing to do here.
    }

    // ----------------------------------------------------------------
    //  Simplify trivial assignments (x = x;)
    // ----------------------------------------------------------------
    public static void SimplifyRedundantAssign(IR.FunctionIR fn)
    {
        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
            {
                if (bb.Statements[i] is IR.AssignStmt a)
                {
                    if (a.Lhs is IR.RegExpr rl && a.Rhs is IR.RegExpr rr && rl.Name == rr.Name)
                        bb.Statements[i] = new IR.NopStmt();
                }
            }
        }
    }

    // ----------------------------------------------------------------
    //  Pass H: Frame object clustering & RSP aliasing
    // ----------------------------------------------------------------
    public static void FrameObjectClusteringAndRspAlias(IR.FunctionIR fn)
    {
        var clusters = new List<(long Base, long Size, string Name)>();

        // 1) Find memset((void*)(rsp + K), 0, N)
        foreach (var bb in fn.Blocks)
        {
            foreach (var s in bb.Statements)
            {
                if (s is not IR.CallStmt cs) continue;
                if (cs.Call.Target.Symbol is null) continue;
                if (!string.Equals(cs.Call.Target.Symbol, "memset", StringComparison.OrdinalIgnoreCase)) continue;
                if (cs.Call.Args.Count < 3) continue;

                if (!IsZero(cs.Call.Args[1])) continue;
                if (!TryGetConst(cs.Call.Args[2], out long size) || size <= 0) continue;

                var dst = StripCasts(cs.Call.Args[0]);
                if (TryMatchRspPlusConst(dst, out long baseOff))
                {
                    int idx = clusters.FindIndex(c => c.Base == baseOff);
                    if (idx >= 0)
                    {
                        var old = clusters[idx];
                        clusters[idx] = (old.Base, Math.Max(old.Size, size), old.Name);
                    }
                    else
                    {
                        clusters.Add((baseOff, size, $"frame_0x{baseOff:X}"));
                    }
                }
            }
        }
        if (clusters.Count == 0) return;

        // 2) Create locals for each cluster
        foreach (var c in clusters.OrderBy(c => c.Base))
        {
            var init = new IR.CastExpr(
                new IR.BinOpExpr(IR.BinOp.Add, new IR.RegExpr("rsp"), new IR.Const(c.Base, 64)),
                new IR.PointerType(IR.X.U8), IR.CastKind.Reinterpret);

            fn.Locals.Add(new IR.LocalVar(c.Name, new IR.PointerType(IR.X.U8), init));
        }

        var map = clusters.ToDictionary(c => c.Base, c => (c.Size, c.Name));

        // 3) Rewrite (rsp + C) into frame_0xBASE + (C-BASE) when inside a cluster
        IR.Expr Rewriter(IR.Expr e)
        {
            switch (e)
            {
                case IR.BinOpExpr b when b.Op == IR.BinOp.Add:
                {
                    var L = RewriteExpr(b.Left, Rewriter);
                    var R = RewriteExpr(b.Right, Rewriter);

                    if (L is IR.RegExpr rl && rl.Name == "rsp" && TryToConst(R, out long c1) && TryReplace(c1, out var rep1))
                        return rep1;

                    if (R is IR.RegExpr rr && rr.Name == "rsp" && TryToConst(L, out long c2) && TryReplace(c2, out var rep2))
                        return rep2;

                    return new IR.BinOpExpr(IR.BinOp.Add, L, R);
                }

                case IR.CastExpr ce:
                    return new IR.CastExpr(RewriteExpr(ce.Value, Rewriter), ce.TargetType, ce.Kind);

                case IR.LoadExpr ld:
                    return new IR.LoadExpr(RewriteExpr(ld.Address, Rewriter), ld.ElemType, ld.Segment);

                case IR.UnOpExpr u:
                    return new IR.UnOpExpr(u.Op, RewriteExpr(u.Operand, Rewriter));

                case IR.CompareExpr c:
                    return new IR.CompareExpr(c.Op, RewriteExpr(c.Left, Rewriter), RewriteExpr(c.Right, Rewriter));

                case IR.TernaryExpr t:
                    return new IR.TernaryExpr(RewriteExpr(t.Condition, Rewriter), RewriteExpr(t.WhenTrue, Rewriter), RewriteExpr(t.WhenFalse, Rewriter));

                case IR.CallExpr call:
                    return new IR.CallExpr(call.Target, call.Args.Select(a => RewriteExpr(a, Rewriter)).ToList());

                default:
                    return e;
            }

            bool TryToConst(IR.Expr ee, out long v)
            {
                if (ee is IR.Const cc) { v = cc.Value; return true; }
                if (ee is IR.UConst u) { v = unchecked((long)u.Value); return true; }
                v = 0; return false;
            }

            bool TryReplace(long absoluteOff, out IR.Expr replaced)
            {
                foreach (var kv in map)
                {
                    long baseOff = kv.Key;
                    long size = kv.Value.Size;
                    if (absoluteOff >= baseOff && absoluteOff < baseOff + size)
                    {
                        var name = kv.Value.Name;
                        long delta = absoluteOff - baseOff;
                        var baseExpr = new IR.LocalExpr(name);
                        replaced = delta == 0 ? baseExpr
                            : new IR.BinOpExpr(IR.BinOp.Add, baseExpr, new IR.Const(delta, 64));
                        return true;
                    }
                }
                replaced = null!;
                return false;
            }
        }

        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
                bb.Statements[i] = RewriteStmt(bb.Statements[i], e => RewriteExpr(e, Rewriter));
        }

        // ---- local helpers ----
        static bool TryGetConst(IR.Expr e, out long v)
        {
            switch (e)
            {
                case IR.Const c: v = c.Value; return true;
                case IR.UConst uc: v = unchecked((long)uc.Value); return true;
            }
            v = 0; return false;
        }
        static bool IsZero(IR.Expr e)
            => (e is IR.Const c && c.Value == 0) || (e is IR.UConst uc && uc.Value == 0);
        static IR.Expr StripCasts(IR.Expr e) => e is IR.CastExpr ce ? StripCasts(ce.Value) : e;
        static bool TryMatchRspPlusConst(IR.Expr e, out long c)
        {
            if (e is IR.BinOpExpr b && b.Op == IR.BinOp.Add)
            {
                if (b.Left is IR.RegExpr rl && rl.Name == "rsp" && TryGet64(b.Right, out c)) return true;
                if (b.Right is IR.RegExpr rr && rr.Name == "rsp" && TryGet64(b.Left, out c)) return true;
            }
            c = 0; return false;

            static bool TryGet64(IR.Expr ee, out long v)
            {
                if (ee is IR.Const cc) { v = cc.Value; return true; }
                if (ee is IR.UConst uc) { v = unchecked((long)uc.Value); return true; }
                v = 0; return false;
            }
        }
    }

    // ----------------------------------------------------------------
    //  Pass E (cleanup): drop redundant "__pseudo(CF = bit(...))"
    // ----------------------------------------------------------------
    public static void DropRedundantBitTestPseudo(IR.FunctionIR fn)
    {
        foreach (var bb in fn.Blocks)
        {
            var outStmts = new List<IR.Stmt>(bb.Statements.Count);
            foreach (var s in bb.Statements)
            {
                if (s is IR.PseudoStmt p && p.Text.StartsWith("CF = bit(", StringComparison.Ordinal))
                    continue; // remove it
                outStmts.Add(s);
            }
            bb.Statements.Clear();
            bb.Statements.AddRange(outStmts);
        }
    }

    // ----------------------------------------------------------------
    //  Pass 8 extension: map "ret = <const>;" to SymConst (NTSTATUS)
    // ----------------------------------------------------------------
    public static void MapNamedRetAssignConstants(IR.FunctionIR fn, IConstantNameProvider provider, string enumFullName)
    {
        static bool IsRetLhs(IR.Expr e)
            => e is IR.RegExpr r &&
               (string.Equals(r.Name, "ret", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Name, "rax", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Name, "eax", StringComparison.OrdinalIgnoreCase));

        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
            {
                if (bb.Statements[i] is IR.AssignStmt a && IsRetLhs(a.Lhs))
                {
                    if (TryEvalULong(a.Rhs, out var v) && provider.TryFormatValue(enumFullName, v, out var name))
                    {
                        bb.Statements[i] = new IR.AssignStmt(a.Lhs, new IR.SymConst(v, 32, name));
                    }
                }
            }
        }

        static bool TryEvalULong(IR.Expr e, out ulong v)
        {
            switch (e)
            {
                case IR.UConst uc: v = uc.Value; return true;
                case IR.Const c: v = unchecked((ulong)c.Value); return true;
                case IR.SymConst sc: v = sc.Value; return true;
            }
            v = 0; return false;
        }
    }

    // ----------------------------------------------------------------
    //  Map return immediate to SymConst (if ReturnStmt uses a const)
    // ----------------------------------------------------------------
    public static void MapNamedReturnConstants(IR.FunctionIR fn, IConstantNameProvider provider, string enumFullName)
    {
        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
            {
                if (bb.Statements[i] is IR.ReturnStmt r && r.Value is not null)
                {
                    if (TryEvalULong(r.Value, out var v) && provider.TryFormatValue(enumFullName, v, out var name))
                        bb.Statements[i] = new IR.ReturnStmt(new IR.SymConst(v, 32, name));
                }
            }
        }

        static bool TryEvalULong(IR.Expr e, out ulong v)
        {
            switch (e)
            {
                case IR.UConst uc: v = uc.Value; return true;
                case IR.Const c: v = unchecked((ulong)c.Value); return true;
                case IR.SymConst sc: v = sc.Value; return true;
            }
            v = 0; return false;
        }
    }

    // ----------------------------------------------------------------
    //  Simplify arithmetic identities and boolean patterns
    // ----------------------------------------------------------------
    public static void SimplifyArithmeticIdentities(IR.FunctionIR fn)
    {
        IR.Expr Rewriter(IR.Expr e)
        {
            switch (e)
            {
                case IR.BinOpExpr b:
                {
                    var L = RewriteExpr(b.Left, Rewriter);
                    var R = RewriteExpr(b.Right, Rewriter);

                    if (b.Op == IR.BinOp.Add)
                    {
                        if (IsZero(L)) return R;
                        if (IsZero(R)) return L;
                    }
                    else if (b.Op == IR.BinOp.Sub)
                    {
                        if (IsZero(R)) return L;
                        if (ExpressionsEqual(L, R)) return MakeZeroFrom(L, R);
                    }
                    else if (b.Op == IR.BinOp.Mul)
                    {
                        if (IsZero(L) || IsZero(R)) return MakeZeroFrom(L, R);
                        if (IsOne(L)) return R;
                        if (IsOne(R)) return L;
                    }
                    else if (b.Op == IR.BinOp.UDiv || b.Op == IR.BinOp.SDiv)
                    {
                        if (IsOne(R)) return L;
                    }
                    else if (b.Op == IR.BinOp.And)
                    {
                        if (IsZero(L) || IsZero(R)) return MakeZeroFrom(L, R);
                        if (IsAllOnes(L)) return R;
                        if (IsAllOnes(R)) return L;
                    }
                    else if (b.Op == IR.BinOp.Or)
                    {
                        if (IsZero(L)) return R;
                        if (IsZero(R)) return L;
                    }
                    else if (b.Op == IR.BinOp.Xor)
                    {
                        if (IsZero(L)) return R;
                        if (IsZero(R)) return L;
                        if (ExpressionsEqual(L, R)) return MakeZeroFrom(L, R);
                    }
                    else if (b.Op == IR.BinOp.Shl || b.Op == IR.BinOp.Shr || b.Op == IR.BinOp.Sar)
                    {
                        if (IsZero(R)) return L;
                    }

                    return new IR.BinOpExpr(b.Op, L, R);
                }

                case IR.UnOpExpr u:
                    return new IR.UnOpExpr(u.Op, RewriteExpr(u.Operand, Rewriter));

                case IR.TernaryExpr t:
                    return new IR.TernaryExpr(RewriteExpr(t.Condition, Rewriter),
                        RewriteExpr(t.WhenTrue, Rewriter),
                        RewriteExpr(t.WhenFalse, Rewriter));

                case IR.CompareExpr c:
                    return new IR.CompareExpr(c.Op, RewriteExpr(c.Left, Rewriter), RewriteExpr(c.Right, Rewriter));

                case IR.CastExpr ce:
                    return new IR.CastExpr(RewriteExpr(ce.Value, Rewriter), ce.TargetType, ce.Kind);

                case IR.LoadExpr ld:
                    return new IR.LoadExpr(RewriteExpr(ld.Address, Rewriter), ld.ElemType, ld.Segment);

                case IR.CallExpr call:
                    return new IR.CallExpr(call.Target, call.Args.Select(a => RewriteExpr(a, Rewriter)).ToList());

                default:
                    return e;
            }

            static bool IsZero(IR.Expr x) => x is IR.Const c && c.Value == 0 || x is IR.UConst uc && uc.Value == 0;
            static bool IsOne(IR.Expr x) => x is IR.Const c && c.Value == 1 || x is IR.UConst uc && uc.Value == 1;
            static bool IsAllOnes(IR.Expr x)
            {
                if (x is IR.Const c) return c.Value == -1;
                if (x is IR.UConst uc)
                    return uc.Bits >= 64 ? uc.Value == ulong.MaxValue : uc.Value == ((1UL << (int)uc.Bits) - 1);
                return false;
            }
            static bool ExpressionsEqual(IR.Expr a, IR.Expr b) => a.Equals(b);

            static IR.Expr MakeZeroFrom(IR.Expr a, IR.Expr b)
            {
                int bits = GetBits(a) ?? GetBits(b) ?? 32;
                return new IR.Const(0, bits);
            }
            static int? GetBits(IR.Expr e) => e switch
            {
                IR.Const c => c.Bits,
                IR.UConst uc => (int)uc.Bits,
                _ => null
            };
        }

        foreach (var bb in fn.Blocks)
            for (int i = 0; i < bb.Statements.Count; i++)
                bb.Statements[i] = RewriteStmt(bb.Statements[i], e => RewriteExpr(e, Rewriter));
    }

    // ----------------------------------------------------------------
    //  Simplify ternary boolean idioms
    // ----------------------------------------------------------------
    public static void SimplifyBooleanTernary(IR.FunctionIR fn)
    {
        IR.Expr Rewriter(IR.Expr e)
        {
            switch (e)
            {
                case IR.TernaryExpr t:
                {
                    var cond = RewriteExpr(t.Condition, Rewriter);
                    var whenTrue = RewriteExpr(t.WhenTrue, Rewriter);
                    var whenFalse = RewriteExpr(t.WhenFalse, Rewriter);

                    if (IsOne(whenTrue) && IsZero(whenFalse))
                        return cond;
                    if (IsZero(whenTrue) && IsOne(whenFalse))
                        return new IR.UnOpExpr(IR.UnOp.LNot, cond);
                    if (whenTrue.Equals(whenFalse))
                        return IsSideEffectFree(cond) ? whenTrue : new IR.TernaryExpr(cond, whenTrue, whenFalse);

                    return new IR.TernaryExpr(cond, whenTrue, whenFalse);
                }

                case IR.BinOpExpr b:
                    return new IR.BinOpExpr(b.Op, RewriteExpr(b.Left, Rewriter), RewriteExpr(b.Right, Rewriter));

                case IR.UnOpExpr u:
                    return new IR.UnOpExpr(u.Op, RewriteExpr(u.Operand, Rewriter));

                case IR.CompareExpr c:
                    return new IR.CompareExpr(c.Op, RewriteExpr(c.Left, Rewriter), RewriteExpr(c.Right, Rewriter));

                case IR.CastExpr ce:
                    return new IR.CastExpr(RewriteExpr(ce.Value, Rewriter), ce.TargetType, ce.Kind);

                case IR.LoadExpr ld:
                    return new IR.LoadExpr(RewriteExpr(ld.Address, Rewriter), ld.ElemType, ld.Segment);

                case IR.CallExpr call:
                    return new IR.CallExpr(call.Target, call.Args.Select(a => RewriteExpr(a, Rewriter)).ToList());

                default:
                    return e;
            }

            static bool IsZero(IR.Expr x) => x is IR.Const c && c.Value == 0 || x is IR.UConst uc && uc.Value == 0;
            static bool IsOne(IR.Expr x) => x is IR.Const c && c.Value == 1 || x is IR.UConst uc && uc.Value == 1;
            static bool IsSideEffectFree(IR.Expr e) => e switch
            {
                IR.Const or IR.UConst or IR.SymConst or IR.RegExpr or IR.ParamExpr or IR.LocalExpr or IR.SegmentBaseExpr => true,
                IR.AddrOfExpr a => IsSideEffectFree(a.Operand),
                IR.BinOpExpr b => IsSideEffectFree(b.Left) && IsSideEffectFree(b.Right),
                IR.UnOpExpr u => IsSideEffectFree(u.Operand),
                IR.CompareExpr c => IsSideEffectFree(c.Left) && IsSideEffectFree(c.Right),
                IR.TernaryExpr t => IsSideEffectFree(t.Condition) && IsSideEffectFree(t.WhenTrue) && IsSideEffectFree(t.WhenFalse),
                IR.CastExpr ce => IsSideEffectFree(ce.Value),
                IR.LabelRefExpr _ => true,
                _ => false,
            };
        }

        foreach (var bb in fn.Blocks)
            for (int i = 0; i < bb.Statements.Count; i++)
                bb.Statements[i] = RewriteStmt(bb.Statements[i], e => RewriteExpr(e, Rewriter));
    }

    // ----------------------------------------------------------------
    //  Simplify logical NOT patterns
    // ----------------------------------------------------------------
    public static void SimplifyLogicalNots(IR.FunctionIR fn)
    {
        IR.Expr Rewriter(IR.Expr e)
        {
            switch (e)
            {
                case IR.UnOpExpr u when u.Op == IR.UnOp.LNot:
                {
                    var inner = RewriteExpr(u.Operand, Rewriter);
                    switch (inner)
                    {
                        case IR.UnOpExpr uu when uu.Op == IR.UnOp.LNot:
                            return uu.Operand;
                        case IR.CompareExpr cmp:
                            return new IR.CompareExpr(Invert(cmp.Op), cmp.Left, cmp.Right);
                        default:
                            return new IR.UnOpExpr(IR.UnOp.LNot, inner);
                    }
                }

                case IR.BinOpExpr b:
                    return new IR.BinOpExpr(b.Op, RewriteExpr(b.Left, Rewriter), RewriteExpr(b.Right, Rewriter));

                case IR.UnOpExpr u:
                    return new IR.UnOpExpr(u.Op, RewriteExpr(u.Operand, Rewriter));

                case IR.CompareExpr c:
                    return new IR.CompareExpr(c.Op, RewriteExpr(c.Left, Rewriter), RewriteExpr(c.Right, Rewriter));

                case IR.TernaryExpr t:
                    return new IR.TernaryExpr(RewriteExpr(t.Condition, Rewriter), RewriteExpr(t.WhenTrue, Rewriter), RewriteExpr(t.WhenFalse, Rewriter));

                case IR.CastExpr ce:
                    return new IR.CastExpr(RewriteExpr(ce.Value, Rewriter), ce.TargetType, ce.Kind);

                case IR.LoadExpr ld:
                    return new IR.LoadExpr(RewriteExpr(ld.Address, Rewriter), ld.ElemType, ld.Segment);

                case IR.CallExpr call:
                    return new IR.CallExpr(call.Target, call.Args.Select(a => RewriteExpr(a, Rewriter)).ToList());

                default:
                    return e;
            }

            static IR.CmpOp Invert(IR.CmpOp op) => op switch
            {
                IR.CmpOp.EQ => IR.CmpOp.NE,
                IR.CmpOp.NE => IR.CmpOp.EQ,
                IR.CmpOp.SLT => IR.CmpOp.SGE,
                IR.CmpOp.SLE => IR.CmpOp.SGT,
                IR.CmpOp.SGT => IR.CmpOp.SLE,
                IR.CmpOp.SGE => IR.CmpOp.SLT,
                IR.CmpOp.ULT => IR.CmpOp.UGE,
                IR.CmpOp.ULE => IR.CmpOp.UGT,
                IR.CmpOp.UGT => IR.CmpOp.ULE,
                IR.CmpOp.UGE => IR.CmpOp.ULT,
                _ => op
            };
        }

        foreach (var bb in fn.Blocks)
            for (int i = 0; i < bb.Statements.Count; i++)
                bb.Statements[i] = RewriteStmt(bb.Statements[i], e => RewriteExpr(e, Rewriter));
    }
}
