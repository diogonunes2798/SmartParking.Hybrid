using Mono.Cecil;
using Mono.Cecil.Cil;
using SmartParking.Application.Abstractions.Documents;
using static SmartParking.ArchitectureTests.SolutionAssemblies;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>
/// The occupancy event log is append-only (D3; spec 003, AC-15). NetArchTest sees dependencies between types, not
/// which methods are called. So the IL of every type in <c>Infrastructure.Documents</c> is read with Mono.Cecil, which
/// NetArchTest already brings in. This includes the async state machines and lambdas the compiler nests in those types.
/// </summary>
public sealed class AppendOnlyEventLogTests
{
    private const string DocumentsNamespace = InfrastructureNamespace + ".Documents";

    private static readonly string[] CollectionTypes =
    [
        "MongoDB.Driver.IMongoCollection`1",
        "MongoDB.Driver.IMongoCollectionExtensions",
    ];

    private static readonly string[] ForbiddenPrefixes = ["Update", "Replace", "Delete", "FindOneAnd", "BulkWrite"];

    [Fact]
    public void DocumentsSublayer_Inspected_NeverChangesOrDeletesDocuments_AC15()
    {
        // Arrange
        var calls = CollectionCalls().ToList();
        Assert.Contains(calls, call => call.Method.StartsWith("InsertMany", StringComparison.Ordinal));

        // Act
        var forbidden = calls
            .Where(call => ForbiddenPrefixes.Any(prefix => call.Method.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(call => $"{call.Caller} calls {call.Method}");

        // Assert
        Assert.Empty(forbidden);
    }

    [Fact]
    public void EventLogPort_Inspected_OnlyAppends_AC15()
    {
        // Act
        var methods = typeof(IOccupancyEventLog).GetMethods().Select(method => method.Name);

        // Assert
        Assert.Equal(["AppendAsync"], methods);
    }

    /// <summary>Every call or virtual call to the MongoDB collection API from the Documents sublayer.</summary>
    private static IEnumerable<(string Caller, string Method)> CollectionCalls()
    {
        using var module = ModuleDefinition.ReadModule(InfrastructureAssembly.Location);
        var types = module.Types
            .Where(type => type.Namespace.StartsWith(DocumentsNamespace, StringComparison.Ordinal))
            .SelectMany(WithNested)
            .ToList();
        Assert.NotEmpty(types);

        return
        [
            .. from type in types
               from method in type.Methods
               where method.HasBody
               from instruction in method.Body.Instructions
               where instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt
               let target = (MethodReference)instruction.Operand
               where CollectionTypes.Contains(target.DeclaringType.GetElementType().FullName)
               select (method.FullName, target.Name),
        ];
    }

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type) =>
        [type, .. type.NestedTypes.SelectMany(WithNested)];
}
