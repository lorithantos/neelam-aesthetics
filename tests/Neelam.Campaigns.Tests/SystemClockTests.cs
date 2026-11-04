using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// House rule 7, enforced (owner, 2026-11-03: "For coding standards tests must not rely on the system
/// clock"): no code in this test assembly reads the machine's clock or waits for time to pass on it.
/// Read from the compiled IL, every call, construction and delegate taken in every method, the code
/// the compiler writes for lambdas and async methods included, so a comment or a string can never trip
/// it and no use can hide from it. A test's clock is <see cref="ManualClock"/>, moved by the test; a
/// wait for work done elsewhere is on a signal (<see cref="Pulse"/>, <see cref="Eventually"/>).
/// </summary>
/// <remarks>
/// Allowed on purpose: a time limit on a wait for a signal (<c>Task.WaitAsync(TimeSpan)</c>,
/// <c>CountdownEvent.Wait(TimeSpan)</c>), which only ever fails a test that would otherwise hang and
/// never decides one that passes; and anything given a <see cref="TimeProvider"/>, which is then the
/// test's own clock, since <c>TimeProvider.System</c> is refused.
/// </remarks>
public class SystemClockTests
{
    [Fact]
    public void No_test_reads_the_system_clock_or_waits_on_it()
    {
        List<string> broken = TestAssembly.Uses()
            .Where(SystemClock.Forbids)
            .Select(use => $"{use.In} uses {use.Member}({string.Join(", ", use.Parameters)})")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(broken.Count == 0,
            "Tests never rely on the system clock (house rule 7): give the code a ManualClock and move it, or wait on a signal.\n"
            + string.Join("\n", broken));
    }

    // The scan sees what it is meant to: calls in the compiler's async state machines, constructions,
    // and the overloads that take a clock, which it lets through.
    [Fact]
    public void The_scan_reads_every_call_compiler_written_code_included()
    {
        IReadOnlyList<MemberUse> uses = TestAssembly.Uses();

        Assert.Contains(uses, u => u.In.StartsWith("Neelam.Campaigns.Tests.Eventually+<Until>", StringComparison.Ordinal)
            && u.Member == "System.Threading.Tasks.Task::WaitAsync");
        MemberUse clocked = uses.First(u => u.In.EndsWith(".A_sweep_interval_the_timer_cannot_wait_is_refused", StringComparison.Ordinal)
            && u.Member == "System.Threading.PeriodicTimer::.ctor");
        Assert.Equal(["System.TimeSpan", "System.TimeProvider"], clocked.Parameters);
        Assert.False(SystemClock.Forbids(clocked));
    }

    [Theory]
    [InlineData("System.DateTime", "get_UtcNow", "", true)]
    [InlineData("System.DateTime", "get_Now", "", true)]
    [InlineData("System.DateTimeOffset", "get_UtcNow", "", true)]
    [InlineData("System.DateTimeOffset", "get_Now", "", true)]
    [InlineData("System.TimeProvider", "get_System", "", true)]
    [InlineData("System.Threading.Tasks.Task", "Delay", "System.Int32", true)]
    [InlineData("System.Threading.Tasks.Task", "Delay", "System.TimeSpan,System.Threading.CancellationToken", true)]
    [InlineData("System.Threading.Tasks.Task", "Delay", "System.TimeSpan,System.TimeProvider,System.Threading.CancellationToken", false)]
    [InlineData("System.Threading.Thread", "Sleep", "System.Int32", true)]
    [InlineData("System.Threading.Timer", ".ctor", "System.Threading.TimerCallback", true)]
    [InlineData("System.Timers.Timer", ".ctor", "System.Double", true)]
    [InlineData("System.Threading.PeriodicTimer", ".ctor", "System.TimeSpan", true)]
    [InlineData("System.Threading.PeriodicTimer", ".ctor", "System.TimeSpan,System.TimeProvider", false)]
    [InlineData("System.Threading.CancellationTokenSource", ".ctor", "System.TimeSpan", true)]
    [InlineData("System.Threading.CancellationTokenSource", ".ctor", "System.TimeSpan,System.TimeProvider", false)]
    [InlineData("System.Threading.CancellationTokenSource", ".ctor", "", false)]
    [InlineData("System.Threading.CancellationTokenSource", "CancelAfter", "System.TimeSpan", true)]
    [InlineData("System.Diagnostics.Stopwatch", "StartNew", "", true)]
    [InlineData("System.Diagnostics.Stopwatch", "GetTimestamp", "", true)]
    [InlineData("System.Environment", "get_TickCount64", "", true)]
    [InlineData("System.Threading.Tasks.Task", "WaitAsync", "System.TimeSpan", false)]
    [InlineData("System.DateTimeOffset", "op_Addition", "System.DateTimeOffset,System.TimeSpan", false)]
    public void What_counts_as_the_system_clock(string type, string name, string parameters, bool forbidden) =>
        Assert.Equal(forbidden, SystemClock.Forbids(new MemberUse("A.test", type, name, parameters.Length == 0 ? [] : parameters.Split(','))));
}

/// <summary>One use of a member of another assembly, in a method of this one.</summary>
/// <param name="In">The method it is in, by its type's full name.</param>
/// <param name="Type">The member's type, by full name, a generic one without its arguments.</param>
/// <param name="Name">The member, as the IL names it: <c>get_UtcNow</c>, <c>.ctor</c>.</param>
/// <param name="Parameters">Its parameters' types, by full name.</param>
internal sealed record MemberUse(string In, string Type, string Name, IReadOnlyList<string> Parameters)
{
    public string Member => this.Type + "::" + this.Name;
}

/// <summary>What reads the machine's clock, or waits for time to pass on it.</summary>
internal static class SystemClock
{
    // Any use at all.
    private static readonly HashSet<string> Always = new(StringComparer.Ordinal)
    {
        "System.DateTime::get_Now",
        "System.DateTime::get_UtcNow",
        "System.DateTime::get_Today",
        "System.DateTimeOffset::get_Now",
        "System.DateTimeOffset::get_UtcNow",
        "System.TimeProvider::get_System",
        "System.Threading.Thread::Sleep",
        "System.Threading.Timer::.ctor",
        "System.Timers.Timer::.ctor",
        "System.Threading.CancellationTokenSource::CancelAfter",
        "System.Environment::get_TickCount",
        "System.Environment::get_TickCount64",
    };

    // Any member of these.
    private static readonly HashSet<string> AlwaysTypes = new(StringComparer.Ordinal) { "System.Diagnostics.Stopwatch" };

    // When given a length of time and no TimeProvider to measure it on.
    private static readonly HashSet<string> UnlessOnAClock = new(StringComparer.Ordinal)
    {
        "System.Threading.Tasks.Task::Delay",
        "System.Threading.PeriodicTimer::.ctor",
        "System.Threading.CancellationTokenSource::.ctor",
    };

    private static readonly HashSet<string> Lengths = new(StringComparer.Ordinal) { "System.TimeSpan", "System.Int32", "System.Int64" };

    public static bool Forbids(MemberUse use) =>
        AlwaysTypes.Contains(use.Type)
        || Always.Contains(use.Member)
        || (UnlessOnAClock.Contains(use.Member)
            && use.Parameters.Any(Lengths.Contains)
            && !use.Parameters.Contains("System.TimeProvider"));
}

/// <summary>
/// This test assembly's IL, read with System.Reflection.Metadata: each method body's calls,
/// constructions and delegates (call, callvirt, newobj, ldftn, ldvirtftn), by the member they name.
/// </summary>
internal static class TestAssembly
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => (ushort)op.Value);

    public static IReadOnlyList<MemberUse> Uses()
    {
        using FileStream file = File.OpenRead(typeof(TestAssembly).Assembly.Location);
        using PEReader pe = new(file);
        MetadataReader metadata = pe.GetMetadataReader();
        Names names = new();
        List<MemberUse> uses = [];
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }
                string where = names.GetTypeFromDefinition(metadata, typeHandle, 0) + "." + metadata.GetString(method.Name);
                foreach (EntityHandle token in Targets(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                {
                    if (Describe(metadata, names, token, where) is { } use)
                    {
                        uses.Add(use);
                    }
                }
            }
        }
        return uses;
    }

    // The member each call, construction or delegate in a method body names.
    private static List<EntityHandle> Targets(BlobReader il)
    {
        List<EntityHandle> targets = [];
        while (il.RemainingBytes > 0)
        {
            ushort value = il.ReadByte();
            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il.ReadByte());
            }
            OpCode op = Codes[value];
            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    targets.Add(MetadataTokens.EntityHandle(il.ReadInt32()));
                    break;
                case OperandType.InlineSwitch:
                    // The count first, then that many targets.
                    int cases = il.ReadInt32();
                    il.Offset += 4 * cases;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineNone:
                    break;
                default:
                    il.Offset += 4;
                    break;
            }
        }
        return targets;
    }

    // A member of another assembly, as used; null for this assembly's own methods.
    private static MemberUse? Describe(MetadataReader metadata, Names names, EntityHandle token, string where)
    {
        switch (token.Kind)
        {
            case HandleKind.MethodSpecification:
                return Describe(metadata, names, metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method, where);
            case HandleKind.MemberReference:
                MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)token);
                if (member.GetKind() != MemberReferenceKind.Method)
                {
                    return null;
                }
                string type = member.Parent.Kind switch
                {
                    HandleKind.TypeReference => names.GetTypeFromReference(metadata, (TypeReferenceHandle)member.Parent, 0),
                    HandleKind.TypeSpecification => metadata.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(names, null),
                    HandleKind.TypeDefinition => names.GetTypeFromDefinition(metadata, (TypeDefinitionHandle)member.Parent, 0),
                    _ => "",
                };
                MethodSignature<string> signature = member.DecodeMethodSignature(names, null);
                return new MemberUse(where, type, metadata.GetString(member.Name), signature.ParameterTypes);
            default:
                return null;
        }
    }

    /// <summary>Types by full name; a generic instance by its definition's name alone.</summary>
    private sealed class Names : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string name = reader.GetString(type.Name);
            return type.GetDeclaringType() is { IsNil: false } outer
                ? this.GetTypeFromDefinition(reader, outer, rawTypeKind) + "+" + name
                : Qualified(reader.GetString(type.Namespace), name);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            string name = reader.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? this.GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + name
                : Qualified(reader.GetString(type.Namespace), name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "method pointer";

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        private static string Qualified(string space, string name) => space.Length == 0 ? name : space + "." + name;
    }
}
