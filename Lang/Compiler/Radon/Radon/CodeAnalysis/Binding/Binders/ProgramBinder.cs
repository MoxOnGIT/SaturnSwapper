using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Radon.CodeAnalysis.Binding.Semantics;
using Radon.CodeAnalysis.Binding.Semantics.Expressions;
using Radon.CodeAnalysis.Binding.Semantics.Members;
using Radon.CodeAnalysis.Binding.Semantics.Statements;
using Radon.CodeAnalysis.Binding.Semantics.Types;
using Radon.CodeAnalysis.Symbols;
using Radon.CodeAnalysis.Syntax;
using Radon.CodeAnalysis.Syntax.Nodes;

namespace Radon.CodeAnalysis.Binding.Binders;

internal sealed class ProgramBinder : Binder
{
    private readonly TopLevelStatementCompilationUnitSyntax _syntax;
    private readonly AssemblySymbol _assembly;
    internal ProgramBinder(AssemblyBinder binder, TopLevelStatementCompilationUnitSyntax syntax) 
        : base(binder, syntax.Location)
    {
        _syntax = syntax;
        _assembly = binder.Assembly;
    }

    public override BoundNode Bind(SyntaxNode? node, params object[] args)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }
        
        var modifiers = ImmutableArray.Create(SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.EntryKeyword);
        var programTypeSymbol = new StructSymbol("<$Program>", ImmutableArray<MemberSymbol>.Empty, _assembly,
            modifiers);
        var mainMethodSymbol = new MethodSymbol(programTypeSymbol, "<$Main>", TypeSymbol.Void, 
            ImmutableArray<ParameterSymbol>.Empty, modifiers);

        programTypeSymbol = (StructSymbol)programTypeSymbol.WithMembers(ImmutableArray.Create<MemberSymbol>(mainMethodSymbol));
        var statements = _syntax.Statements;
        var boundStatements = new List<BoundStatement>();
        var statementBinder = new StatementBinder(this, node.Location);
        foreach (var statement in statements)
        {
            var boundStatement = (BoundStatement)statementBinder.Bind(statement, mainMethodSymbol);
            boundStatements.Add(boundStatement);
        }

        var locals = Scope!.Symbols.OfType<LocalVariableSymbol>().ToImmutableArray();
        Diagnostics.AddRange(statementBinder.Diagnostics);
        var boundMainMethod = new BoundMethod(_syntax, mainMethodSymbol, boundStatements.ToImmutableArray(), statementBinder.Locals.Concat(locals).ToImmutableArray());
        var boundProgram = new BoundStruct(_syntax, programTypeSymbol, ImmutableArray.Create<BoundMember>(boundMainMethod));
        return boundProgram;
    }
}