using System.Xml;
using FluentAssertions;
using Multiplayer.Common.StateHashing;

namespace Tests;

public class HashingXmlWriterTest
{
    private static HashingXmlWriter Write(Action<XmlWriter> write, int recordDepth = 4, string[]? cosmetic = null)
    {
        var writer = new HashingXmlWriter(recordDepth, cosmetic);
        writer.WriteStartDocument();
        write(writer);
        writer.WriteEndDocument();
        return writer;
    }

    // A small tree resembling Scribe output:
    // <game><maps><li><things><thing Class="Pawn"><id>7</id><name><value>Cow 5</value></name></thing></things></li></maps></game>
    private static void SampleTree(XmlWriter w, string pawnName = "Cow 5", string id = "7")
    {
        w.WriteStartElement("game");
        w.WriteStartElement("maps");
        w.WriteStartElement("li");
        w.WriteStartElement("things");
        w.WriteStartElement("thing");
        w.WriteAttributeString("Class", "Pawn");
        w.WriteElementString("id", id);
        w.WriteStartElement("name");
        w.WriteElementString("value", pawnName);
        w.WriteEndElement(); // name
        w.WriteEndElement(); // thing
        w.WriteEndElement(); // things
        w.WriteEndElement(); // li
        w.WriteEndElement(); // maps
        w.WriteEndElement(); // game
    }

    [Test]
    public void IdenticalWrites_ProduceIdenticalTrees()
    {
        var first = Write(w => SampleTree(w));
        var second = Write(w => SampleTree(w));

        second.SimRoot.Should().Be(first.SimRoot);
        second.CosmeticRoot.Should().Be(first.CosmeticRoot);
        second.Records.Should().Equal(first.Records);
    }

    [Test]
    public void DeepTextChange_ChangesRootAndAncestors_ButNotSiblings()
    {
        var baseline = Write(w =>
        {
            w.WriteStartElement("game");
            w.WriteStartElement("a");
            w.WriteElementString("x", "1");
            w.WriteEndElement();
            w.WriteStartElement("b");
            w.WriteElementString("y", "2");
            w.WriteEndElement();
            w.WriteEndElement();
        });

        var changed = Write(w =>
        {
            w.WriteStartElement("game");
            w.WriteStartElement("a");
            w.WriteElementString("x", "1!");
            w.WriteEndElement();
            w.WriteStartElement("b");
            w.WriteElementString("y", "2");
            w.WriteEndElement();
            w.WriteEndElement();
        });

        changed.SimRoot.Should().NotBe(baseline.SimRoot);

        ulong Digest(HashingXmlWriter writer, string path) =>
            writer.Records.Single(n => n.Path == path).Digest;

        Digest(changed, "game/a").Should().NotBe(Digest(baseline, "game/a"));
        Digest(changed, "game").Should().NotBe(Digest(baseline, "game"));
        Digest(changed, "game/b").Should().Be(Digest(baseline, "game/b"));
    }

    [Test]
    public void TextAndChildAndAttribute_AreDomainSeparated()
    {
        var text = Write(w =>
        {
            w.WriteStartElement("a");
            w.WriteString("b");
            w.WriteEndElement();
        });

        var child = Write(w =>
        {
            w.WriteStartElement("a");
            w.WriteStartElement("b");
            w.WriteEndElement();
            w.WriteEndElement();
        });

        var attr = Write(w =>
        {
            w.WriteStartElement("a");
            w.WriteAttributeString("b", "");
            w.WriteEndElement();
        });

        text.SimRoot.Should().NotBe(child.SimRoot);
        text.SimRoot.Should().NotBe(attr.SimRoot);
        child.SimRoot.Should().NotBe(attr.SimRoot);
    }

    [Test]
    public void AttributeValueChange_ChangesDigest()
    {
        var a = Write(w =>
        {
            w.WriteStartElement("thing");
            w.WriteAttributeString("Class", "Pawn");
            w.WriteEndElement();
        });

        var b = Write(w =>
        {
            w.WriteStartElement("thing");
            w.WriteAttributeString("Class", "Corpse");
            w.WriteEndElement();
        });

        a.SimRoot.Should().NotBe(b.SimRoot);
    }

    [Test]
    public void RecordDepth_LimitsRecordedNodes_WithoutChangingRoot()
    {
        var deep = Write(w => SampleTree(w), recordDepth: 4);
        var shallow = Write(w => SampleTree(w), recordDepth: 1);

        shallow.SimRoot.Should().Be(deep.SimRoot);

        deep.Records.Select(n => n.Path).Should().Contain("game/maps/li/things/thing");
        shallow.Records.Select(n => n.Path).Should().BeEquivalentTo("game", "game/maps");
    }

    [Test]
    public void RepeatedSiblings_GetIndexedPaths()
    {
        var writer = Write(w =>
        {
            w.WriteStartElement("maps");
            w.WriteStartElement("li");
            w.WriteEndElement();
            w.WriteStartElement("li");
            w.WriteEndElement();
            w.WriteStartElement("li");
            w.WriteEndElement();
            w.WriteEndElement();
        });

        writer.Records.Select(n => n.Path).Should()
            .BeEquivalentTo("maps", "maps/li", "maps/li[1]", "maps/li[2]");
    }

    [Test]
    public void ChildrenAreRecordedBeforeParents()
    {
        var writer = Write(w => SampleTree(w));
        var paths = writer.Records.Select(n => n.Path).ToList();

        paths.IndexOf("game/maps/li/things/thing").Should()
            .BeLessThan(paths.IndexOf("game/maps/li/things"));
        paths.IndexOf("game/maps").Should().BeLessThan(paths.IndexOf("game"));
    }

    [Test]
    public void CosmeticContent_AffectsOnlyCosmeticChannel()
    {
        string[] cosmetic = ["name"];
        var cow = Write(w => SampleTree(w, pawnName: "Cow 5"), cosmetic: cosmetic);
        var vaca = Write(w => SampleTree(w, pawnName: "Vaca 5"), cosmetic: cosmetic);

        vaca.SimRoot.Should().Be(cow.SimRoot, "language-generated name content must not affect the sim channel");
        vaca.CosmeticRoot.Should().NotBe(cow.CosmeticRoot, "the cosmetic channel must expose the name divergence");
        vaca.Records.Should().Equal(cow.Records);

        // The diverged cosmetic subtree is identifiable by path
        var cowName = cow.CosmeticRecords.Single(n => n.Path == "game/maps/li/things/thing/name");
        var vacaName = vaca.CosmeticRecords.Single(n => n.Path == "game/maps/li/things/thing/name");
        vacaName.Digest.Should().NotBe(cowName.Digest);
    }

    [Test]
    public void CosmeticElementPresence_StillAffectsSimChannel()
    {
        string[] cosmetic = ["name"];
        var with = Write(w =>
        {
            w.WriteStartElement("thing");
            w.WriteStartElement("name");
            w.WriteEndElement();
            w.WriteEndElement();
        }, cosmetic: cosmetic);

        var without = Write(w =>
        {
            w.WriteStartElement("thing");
            w.WriteEndElement();
        }, cosmetic: cosmetic);

        with.SimRoot.Should().NotBe(without.SimRoot,
            "structural presence of a cosmetic element is sim-relevant even though its content isn't");
    }

    [Test]
    public void SimContentChange_DoesNotAffectCosmeticChannel()
    {
        string[] cosmetic = ["name"];
        var a = Write(w => SampleTree(w, id: "7"), cosmetic: cosmetic);
        var b = Write(w => SampleTree(w, id: "8"), cosmetic: cosmetic);

        b.SimRoot.Should().NotBe(a.SimRoot);
        b.CosmeticRoot.Should().Be(a.CosmeticRoot);
    }

    [Test]
    public void WithoutCosmeticElements_EverythingIsSimChannel()
    {
        var cow = Write(w => SampleTree(w, pawnName: "Cow 5"));
        var vaca = Write(w => SampleTree(w, pawnName: "Vaca 5"));

        cow.SimRoot.Should().NotBe(vaca.SimRoot);
        cow.CosmeticRecords.Should().BeEmpty();
    }
}
