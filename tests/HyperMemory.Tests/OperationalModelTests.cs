using HyperMemory.Core;
using HyperMemory.Infrastructure;

namespace HyperMemory.Tests;

public sealed class OperationalModelTests
{
    [Fact]
    public void Operational_features_are_disabled_by_default()
    {
        var options = new HyperMemoryOptions();

        Assert.False(options.Operational.AnyEnabled);
        Assert.False(options.Operational.EnableEventJournal);
        Assert.False(options.Operational.EnableCapabilityRouting);
        Assert.False(options.Operational.EnableToolEventCapture);
    }

    [Fact]
    public void Operational_vocabulary_remains_extensible()
    {
        var reference = new OperationalObjectRef("custom-domain-object", "object-1");
        var relationship = new OperationalRelationship(
            "relation-1",
            reference,
            new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-1"),
            "custom-relation",
            "event-1",
            1,
            true,
            DateTimeOffset.UtcNow);

        Assert.Equal("custom-domain-object", reference.ObjectType);
        Assert.Equal("custom-relation", relationship.RelationshipType);
    }

    [Fact]
    public void Validation_defaults_to_unknown_instead_of_success()
    {
        Assert.Equal(0, (int)ValidationStatus.Unknown);
        Assert.NotEqual(ValidationStatus.Pass, default);
    }
}
