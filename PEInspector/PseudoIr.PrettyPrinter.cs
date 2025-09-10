using System.Globalization;
using System.Text;

public static partial class PseudoIr
{
    public sealed class PrettyPrinter
    {
        public sealed class Options
        {
            public bool EmitHeaderComment { get; set; } = true;
            public bool EmitBlockLabels { get; set; } = false;
            public bool CommentSignednessOnCmp { get; set; } = true;
            public bool UseStdIntNames { get; set; } = true;
            public string Indent { get; set; } = "    ";
            public IConstantNameProvider? ConstantProvider { get; set; }
        }

        private readonly Options _opt;
        private readonly StringBuilder _sb = new();
        private int _indent;

        public PrettyPrinter(Options? opt = null) => _opt = opt ?? new Options();

        public string Print(FunctionIR fn)
        {
            _sb.Clear();
            _indent = 0;

            if (_opt.EmitHeaderComment)
            {
                EmitLine("/*");
                EmitLine(" * Pseudocode (approximate) reconstructed from x64 instructions.");
                EmitLine(" * Assumptions: MSVC on Windows, Microsoft x64 calling convention.");
                EmitLine(" * Parameters: p1 (RCX), p2 (RDX), p3 (R8), p4 (R9); return in RAX.");
                EmitLine(" * NOTE: This is not a full decompiler; conditions, types, and pointer types");
                EmitLine(" *       are inferred heuristically and may be imprecise.");
                EmitLine(" */");
                _sb.AppendLine();
            }

            var ret = RenderType(fn.ReturnType);
            var paramList = string.Join(", ",
                fn.Parameters.OrderBy(p => p.Index).Select(p => $"{RenderType(p.Type)} {p.Name}"));

            EmitLine($"{ret} {fn.Name}({paramList}) {{");
            _indent++;

            bool usesFp = GetTag(fn, "UsesFramePointer", false);
            int localSize = GetTag(fn, "LocalSize", 0);
            if (usesFp)
            {
                if (localSize > 0) EmitLine($"// stack frame: {localSize} bytes of locals (rbp-based)");
                else EmitLine("// stack frame: rbp-based");
            }
            else if (localSize > 0)
            {
                EmitLine($"// stack allocation: sub rsp, {localSize} (no rbp)");
            }

            EmitLine("// registers shown when helpful; memory shown via *(uintXX_t*)(addr)");
            _sb.AppendLine();

            foreach (var l in fn.Locals)
            {
                EmitIndent();
                _sb.Append(RenderType(l.Type)).Append(' ').Append(l.Name);
                if (l.Initializer is not null)
                {
                    _sb.Append(" = ");
                    EmitExpr(l.Initializer, Precedence.Min);
                }

                _sb.AppendLine(";");
            }

            if (fn.Locals.Count > 0) _sb.AppendLine();

            if (fn.StructuredBody is not null)
                EmitHiNode(fn.StructuredBody);
            else
                EmitBlocks(fn);

            _indent--;
            EmitLine("}");
            return _sb.ToString();
        }

        private static TTag GetTag<TTag>(FunctionIR fn, string key, TTag def)
        {
            if (fn.Tags.TryGetValue(key, out var obj) && obj is TTag t) return t;
            return def;
        }

        private void EmitBlocks(FunctionIR fn)
        {
            foreach (var bb in fn.Blocks)
            {
                if (_opt.EmitBlockLabels)
                    EmitLine($"{bb.Label.Name}:");

                foreach (var s in bb.Statements)
                    EmitStmt(s);
            }
        }

        private void EmitHiNode(HiNode n)
        {
            switch (n)
            {
                case SeqNode seq:
                    foreach (var item in seq.Items) EmitHiNode(item);
                    break;

                case StmtNode stmt:
                    EmitStmt(stmt.Statement);
                    break;

                case IfNode ifn:
                    Emit("if (");
                    EmitExpr(ifn.Condition, Precedence.Min);
                    _sb.AppendLine(") {");
                    _indent++;
                    EmitHiNode(ifn.Then);
                    _indent--;
                    if (ifn.Else is not null)
                    {
                        EmitLine("} else {");
                        _indent++;
                        EmitHiNode(ifn.Else);
                        _indent--;
                    }

                    EmitLine("}");
                    break;

                case WhileNode wn:
                    Emit("while (");
                    EmitExpr(wn.Condition, Precedence.Min);
                    _sb.AppendLine(") {");
                    _indent++;
                    EmitHiNode(wn.Body);
                    _indent--;
                    EmitLine("}");
                    break;

                case DoWhileNode dwn:
                    EmitLine("do {");
                    _indent++;
                    EmitHiNode(dwn.Body);
                    _indent--;
                    Emit("} while (");
                    EmitExpr(dwn.Condition, Precedence.Min);
                    _sb.AppendLine(");");
                    break;

                case SwitchNode sw:
                    Emit("switch (");
                    EmitExpr(sw.Scrutinee, Precedence.Min);
                    _sb.AppendLine(") {");
                    _indent++;
                    foreach (var c in sw.Cases)
                    {
                        if (c.IsDefault) EmitLine("default:");
                        else
                        {
                            Emit("case ");
                            if (c.MatchValue is null) _sb.Append("/* null */");
                            else EmitExpr(c.MatchValue, Precedence.Min);
                            _sb.AppendLine(":");
                        }

                        _indent++;
                        foreach (var item in c.Body) EmitHiNode(item);
                        EmitLine("break;");
                        _indent--;
                    }

                    _indent--;
                    EmitLine("}");
                    break;

                default:
                    EmitLine("__pseudo(unsupported_structured_node);");
                    break;
            }
        }

        private void EmitStmt(Stmt s)
        {
            switch (s)
            {
                case AssignStmt a:
                    EmitIndent();
                    if (a.Rhs is CallExpr ce)
                    {
                        _sb.Append("/* call */ ");
                        EmitLValue(a.Lhs);
                        _sb.Append(" = ");
                        EmitCall(ce);
                        _sb.Append(";");
                        if (a.Lhs is RegExpr rx && (string.Equals(rx.Name, "ret", StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(rx.Name, "rax",
                                                        StringComparison.OrdinalIgnoreCase)))
                            _sb.Append("  // RAX");
                        _sb.AppendLine();
                        break;
                    }

                    EmitLValue(a.Lhs);
                    _sb.Append(" = ");
                    EmitExpr(a.Rhs, Precedence.Min);
                    _sb.AppendLine(";");
                    break;

                case StoreStmt st:
                    EmitIndent();
                    EmitMemory(st.ElemType, st.Address, st.Segment);
                    _sb.Append(" = ");
                    EmitExpr(st.Value, Precedence.Min);
                    _sb.AppendLine(";");
                    break;

                case CallStmt cs:
                    EmitIndent();
                    EmitCall(cs.Call);
                    _sb.AppendLine(";");
                    break;

                case IfGotoStmt ig:
                    EmitIndent();
                    _sb.Append("if (");
                    EmitExpr(ig.Condition, Precedence.Min);
                    _sb.Append(") goto ");
                    _sb.Append(ig.Target.Name);
                    _sb.AppendLine(";");
                    break;

                case GotoStmt g:
                    EmitLine($"goto {g.Target.Name};");
                    break;

                case LabelStmt l:
                    EmitLine($"{l.Label.Name}:");
                    break;

                case ReturnStmt r:
                    EmitIndent();
                    if (r.Value is null) _sb.AppendLine("return;");
                    else
                    {
                        _sb.Append("return ");
                        EmitExpr(r.Value, Precedence.Min);
                        _sb.AppendLine(";");
                    }

                    break;

                case AsmCommentStmt c:
                    EmitLine($"/* {c.Text} */");
                    break;

                case PseudoStmt p:
                    EmitIndent();
                    _sb.Append("__pseudo(").Append(p.Text).AppendLine(");");
                    break;

                case NopStmt:
                    EmitIndent();
                    _sb.AppendLine("__pseudo(nop);");
                    break;

                default:
                    EmitIndent();
                    _sb.AppendLine("__pseudo(unsupported_stmt);");
                    break;
            }
        }

        private void EmitCall(CallExpr c)
        {
            if (c.Target.Symbol is not null) _sb.Append(c.Target.Symbol);
            else if (c.Target.Address is not null)
            {
                _sb.Append("(*");
                EmitExpr(c.Target.Address, Precedence.Min);
                _sb.Append(")");
            }
            else _sb.Append("indirect_call");

            _sb.Append("(");
            for (int i = 0; i < c.Args.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                if (_opt.ConstantProvider != null &&
                    _opt.ConstantProvider.TryGetArgExpectedEnumType(c.Target.Symbol, i, out var enumType) &&
                    TryEvalConst(c.Args[i], out var constVal) &&
                    _opt.ConstantProvider.TryFormatValue(enumType, constVal, out var fmt))
                {
                    _sb.Append(fmt);
                }
                else
                {
                    EmitExpr(c.Args[i], Precedence.Min);
                }
            }

            _sb.Append(")");
        }

        private void EmitExpr(Expr e, int parentPrec)
        {
            switch (e)
            {
                case Const c:
                    EmitConst(c.Value, c.Bits);
                    break;

                case UConst uc:
                    EmitUConst(uc.Value, uc.Bits);
                    break;

                case SymConst sc:
                    _sb.Append(sc.Name);
                    break;

                case RegExpr r:
                    _sb.Append(r.Name);
                    break;

                case ParamExpr p:
                    _sb.Append(p.Name);
                    break;

                case LocalExpr l:
                    _sb.Append(l.Name);
                    break;

                case SegmentBaseExpr seg:
                    _sb.Append(seg.Segment == SegmentReg.FS ? "__segfs" : "__seggs");
                    break;

                case AddrOfExpr aof:
                    _sb.Append("&");
                    EmitExpr(aof.Operand, Precedence.Prefix);
                    break;

                case LoadExpr ld:
                    EmitMemory(ld.ElemType, ld.Address, ld.Segment);
                    break;

                case BinOpExpr b:
                {
                    var (opTxt, prec, assocRight) = OpInfo(b.Op);
                    bool need = prec < parentPrec;
                    if (need) _sb.Append("(");
                    EmitExpr(b.Left, assocRight ? prec : prec + 1);
                    _sb.Append(' ').Append(opTxt).Append(' ');
                    EmitExpr(b.Right, assocRight ? prec + 1 : prec);
                    if (need) _sb.Append(")");
                }
                    break;

                case UnOpExpr u:
                {
                    var (opTxt, prec) = UnOpInfo(u.Op);
                    bool need = prec < parentPrec;
                    if (need) _sb.Append("(");
                    _sb.Append(opTxt);
                    EmitExpr(u.Operand, prec);
                    if (need) _sb.Append(")");
                }
                    break;

                case CompareExpr cmp:
                {
                    var (opTxt, signedHint) = CmpInfo(cmp.Op);
                    if (_opt.CommentSignednessOnCmp && signedHint is not null)
                        _sb.Append("/* ").Append(signedHint).Append(" */ ");
                    EmitExpr(cmp.Left, Precedence.Rel);
                    _sb.Append(' ').Append(opTxt).Append(' ');
                    EmitExpr(cmp.Right, Precedence.Rel);
                }
                    break;

                case TernaryExpr t:
                {
                    bool need = Precedence.Cond < parentPrec;
                    if (need) _sb.Append("(");
                    EmitExpr(t.Condition, Precedence.Cond);
                    _sb.Append(" ? ");
                    EmitExpr(t.WhenTrue, Precedence.Cond);
                    _sb.Append(" : ");
                    EmitExpr(t.WhenFalse, Precedence.Cond);
                    if (need) _sb.Append(")");
                }
                    break;

                case CastExpr c:
                {
                    string castTxt = RenderCast(c);
                    bool need = Precedence.Prefix < parentPrec;
                    if (need) _sb.Append("(");
                    _sb.Append(castTxt);
                    EmitExpr(c.Value, Precedence.Prefix);
                    if (need) _sb.Append(")");
                }
                    break;

                case CallExpr ce:
                    EmitCall(ce);
                    break;

                case IntrinsicExpr ie:
                    _sb.Append(ie.Name).Append("(");
                    for (int i = 0; i < ie.Args.Count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        EmitExpr(ie.Args[i], Precedence.Min);
                    }

                    _sb.Append(")");
                    break;

                case LabelRefExpr lr:
                    _sb.Append(lr.Label.Name);
                    break;

                default:
                    _sb.Append("__pseudo(unsupported_expr)");
                    break;
            }
        }

        private static bool TryEvalConst(Expr e, out ulong value)
        {
            switch (e)
            {
                case UConst uc:
                    value = uc.Value;
                    return true;
                case Const c:
                    value = unchecked((ulong)c.Value);
                    return true;
                case SymConst sc:
                    value = sc.Value;
                    return true;
                case BinOpExpr b when b.Op == BinOp.Or:
                    if (TryEvalConst(b.Left, out var l) && TryEvalConst(b.Right, out var r))
                    {
                        value = l | r;
                        return true;
                    }

                    break;
                case BinOpExpr b when b.Op == BinOp.Add:
                    if (TryEvalConst(b.Left, out var l2) && TryEvalConst(b.Right, out var r2))
                    {
                        value = l2 + r2;
                        return true;
                    }

                    break;
            }

            value = 0;
            return false;
        }

        private void EmitLValue(Expr lhs)
        {
            switch (lhs)
            {
                case RegExpr or LocalExpr or ParamExpr:
                    EmitExpr(lhs, Precedence.Min);
                    break;
                case LoadExpr ld:
                    EmitMemory(ld.ElemType, ld.Address, ld.Segment);
                    break;
                default:
                    EmitExpr(lhs, Precedence.Min);
                    break;
            }
        }

        private void EmitMemory(IrType elem, Expr addr, SegmentReg seg)
        {
            _sb.Append("*((").Append(RenderType(elem)).Append("*)(");
            if (seg is SegmentReg.FS) _sb.Append("fs:");
            if (seg is SegmentReg.GS) _sb.Append("gs:");
            EmitExpr(addr, Precedence.Min);
            _sb.Append("))");
        }

        private void EmitConst(long v, int bits)
        {
            if (v < 0) _sb.Append("0x").Append(unchecked((ulong)v).ToString("X"));
            else if (v >= 10) _sb.Append("0x").Append(v.ToString("X"));
            else _sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private void EmitUConst(ulong v, int bits)
        {
            if (v >= 10) _sb.Append("0x").Append(v.ToString("X"));
            else _sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private static class Precedence
        {
            public const int Min = 0,
                Assign = 1,
                Cond = 2,
                Or = 3,
                And = 4,
                Xor = 5,
                BitAnd = 6,
                Rel = 7,
                Shift = 8,
                Add = 9,
                Mul = 10,
                Prefix = 11,
                Atom = 12;
        }

        private static (string op, int prec, bool assocRight) OpInfo(BinOp op) => op switch
        {
            BinOp.Mul => ("*", Precedence.Mul, false),
            BinOp.UDiv or BinOp.SDiv => ("/", Precedence.Mul, false),
            BinOp.URem or BinOp.SRem => ("%", Precedence.Mul, false),
            BinOp.Add => ("+", Precedence.Add, false),
            BinOp.Sub => ("-", Precedence.Add, false),
            BinOp.Shl => ("<<", Precedence.Shift, false),
            BinOp.Shr or BinOp.Sar => (">>", Precedence.Shift, false),
            BinOp.And => ("&", Precedence.BitAnd, false),
            BinOp.Xor => ("^", Precedence.Xor, false),
            BinOp.Or => ("|", Precedence.Or, false),
            _ => ("?", Precedence.Add, false)
        };

        private static (string op, int prec) UnOpInfo(UnOp op) => op switch
        {
            UnOp.Neg => ("-", Precedence.Prefix),
            UnOp.Not => ("~", Precedence.Prefix),
            UnOp.LNot => ("!", Precedence.Prefix),
            _ => ("/* ? */", Precedence.Prefix)
        };

        private static (string op, string? signedHint) CmpInfo(CmpOp op) => op switch
        {
            CmpOp.EQ => ("==", null),
            CmpOp.NE => ("!=", null),
            CmpOp.SLT => ("<", "signed"),
            CmpOp.SLE => ("<=", "signed"),
            CmpOp.SGT => (">", "signed"),
            CmpOp.SGE => (">=", "signed"),
            CmpOp.ULT => ("<", "unsigned"),
            CmpOp.ULE => ("<=", "unsigned"),
            CmpOp.UGT => (">", "unsigned"),
            CmpOp.UGE => (">=", "unsigned"),
            _ => ("/* ? */", null)
        };

        private string RenderCast(CastExpr c) => $"({RenderType(c.TargetType)})";

        private string RenderType(IrType t) => t switch
        {
            VoidType => "void",
            IntType it => RenderIntType(it),
            FloatType ft => ft.Bits == 32 ? "float" : ft.Bits == 64 ? "double" : $"float{ft.Bits}_t",
            PointerType pt => $"{RenderType(pt.Element)}*",
            VectorType vt => vt.Bits switch
            {
                128 => "vec128_t", 256 => "vec256_t", 512 => "vec512_t", _ => $"vec{vt.Bits}_t"
            },
            UnknownType ut => ut.Note is null ? "uint64_t /* unknown */" : $"uint64_t /* {ut.Note} */",
            _ => "uint64_t /* ? */"
        };

        private string RenderIntType(IntType t)
            => _opt.UseStdIntNames
                ? (t.IsSigned ? "int" : "uint") + t.Bits + "_t"
                : t.Bits switch
                {
                    8 => t.IsSigned ? "signed char" : "unsigned char",
                    16 => t.IsSigned ? "short" : "unsigned short",
                    32 => t.IsSigned ? "int" : "unsigned int",
                    64 => t.IsSigned ? "long long" : "unsigned long long",
                    _ => t.IsSigned ? $"int{t.Bits}_t" : $"uint{t.Bits}_t"
                };

        private void Emit(string s) => _sb.Append(s);

        private void EmitLine(string s)
        {
            EmitIndent();
            _sb.AppendLine(s);
        }

        private void EmitIndent()
        {
            for (int i = 0; i < _indent; i++) _sb.Append(_opt.Indent);
        }
    }
}