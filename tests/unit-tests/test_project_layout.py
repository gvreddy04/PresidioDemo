"""Verify the solution layout, the docs folders and immutable finalized sample bytes."""
import hashlib
import re
import unittest
from pipeline_harness import PROJECT, FEEDS

BROKER_PACKAGES = ("solace", "solclient", "ibmmq", "ibm.wmq", "ibm.xms", "rabbitmq", "kafka", "amqp", "activemq",
                   "apache.nms", "azure.messaging", "awssdk.sqs", "awssdk.sns", "masstransit", "nservicebus")


class ProjectLayoutTests(unittest.TestCase):
    def test_docs_holds_only_samples_and_diagrams(self):
        docs = PROJECT / "docs"
        self.assertEqual({p.name for p in docs.iterdir() if p.is_dir()}, {"sample-docs", "diagrams"})
        for obsolete in ["input-feeds", "output-feeds", "failed-feeds", "feed-logs", "feed-processing", "input", "output",
                         "solace-feeds", "batch-runs", ".processing", ".feed-listener.lock"]:
            self.assertFalse((docs / obsolete).exists(), obsolete)

    def test_solution_separates_core_messaging_broker_and_worker(self):
        for project in ["src/PresidioDemo.Core", "src/PresidioDemo.Messaging", "src/PresidioDemo.Messaging.Solace",
                        "src/PresidioDemo.Worker", "tests/PresidioDemo.Messaging.Testing"]:
            folder = PROJECT / project
            self.assertTrue((folder / (folder.name + ".csproj")).is_file(), project)
        self.assertTrue((PROJECT / "src/PresidioDemo.Worker/appsettings.json").is_file())
        core = (PROJECT / "src/PresidioDemo.Core/PresidioDemo.Core.csproj").read_text(encoding="utf-8")
        self.assertNotIn("solclient", core.lower())
        self.assertNotIn("solace", core.lower())
        messaging = (PROJECT / "src/PresidioDemo.Messaging/PresidioDemo.Messaging.csproj").read_text(encoding="utf-8")
        packages = [name.lower() for name in re.findall(r'<PackageReference\s+Include="([^"]+)"', messaging)]
        for package in packages:
            self.assertFalse(any(broker in package for broker in BROKER_PACKAGES), package)
        self.assertNotIn("PresidioDemo.Messaging.Solace", messaging)

    def test_per_feed_parsers_and_policy_xml_are_removed(self):
        core = PROJECT / "src/PresidioDemo.Core"
        for feed in ("ACI", "ETKT", "PNR"):
            for area in ("Parser", "Serializer", "FeedCodecs", "Models", "Reporting"):
                self.assertFalse((core / area / feed).exists(), f"{area}/{feed}")
        for name in ("feed-policies.xml", "xdr-layouts.xml", "reference-layouts.xml"):
            self.assertFalse((core / "Configuration" / name).exists(), name)
        for name in ("XdrFeedCodec.cs", "ReferenceLayoutFeedCodec.cs"):
            self.assertFalse((core / "Parser/Shared" / name).exists(), name)
        recognizers = {p.name for p in (core / "Configuration/recognizers").glob("*.xml")}
        self.assertEqual(recognizers, {"aci.xml", "etkt.xml", "pnr.xml", "pnr-linking.xml", "seats.xml"})

    def test_samples_are_finalized_txt_files_in_one_folder_per_feed_with_unchanged_bytes(self):
        expected = {
            "ACI/ACI.txt": "f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d",
            "ACI/ACI_02.txt": "bdfa61c1999cef2590a535ca6b0049fa30104b1a717537ef413a02480b3bd95c",
            "ACI/ACI_03.txt": "aaacd2a473d3155ed8cc4bd44f9ce00b8494a52be516860561a5112774ab1042",
            "ACI/ACI_04.txt": "3b5cee1e10a83f5da21c7fbce779f8e978a26c2c3aaf2a5ade18b40f136b73b5",
            "ACI/ACI_05.txt": "71c8fde1c93e0bb6f1562d596e58ef8c3618a0a9ee0a72c12bbf42b2da2cecb2",
            "ACI/ACI_06.txt": "9526c95f10f5e0dab53ce2bda1ae24e3cfffbfac0d2c65f15f0c5d5913ef0087",
            "ETKT/ETKT.txt": "6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa",
            "ETKT/ETKT_02.txt": "6a0f5f65a53ff39fee57fdf8dbb870a28dc4fe136296ba4c151c277e99b78cfa",
            "ETKT/ETKT_03.txt": "aeed5bf95815a1358dde1388a34bdd1c0d93d8ef9a52dee8e4cf1620cfc6ee64",
            "ETKT/ETKT_04.txt": "14722ed6d86806b17e21a186e1fcd239f1c02923818d80998ee72d96d440d1db",
            "ETKT/ETKT_05.txt": "231943ee46a57c5f1c6e1d5ddf2cf0508d9560638cc05d9da7ca69446204ad2e",
            "ETKT/ETKT_06.txt": "ecde914276f406c9e32127f7f056973bc9deb30184747e1f6ac93484ab794266",
            "PNR/PNR.txt": "75cc60895384395f8df28eff4c4abf6a0c58f1dc7960c5eee41cb294def0e4de",
            "PNR/PNR_02.txt": "99142486d0d1b4b463363057f2822365a3f9fa567d6f1116bc881ce1d9808157",
            "PNR-Linking/PNR_LINKING.txt": "2a8caf4538eee4a2232bdcc939adf427171d5ab35a6b380059b2e09eb5c03610",
            "PNR-Linking/PNR_LINKING_02_Negative_Testing.txt": "ad14c3dfe6e4e98e49e008d1989135cc367ac944f137de0e831f2418eae0aa73",
            "PNR-Linking/PNR_LINKING_03_Negative_Testing.txt": "d581ee11879d8c3a487d9476c34ff64f54e85043f6ed0261086a85cdf18c3738",
            "Seats/SEATS_03.txt": "0b9bd0aa4d6be9fef748049b9c5133122e81b02fc3306cc07a2b41f149fd5575",
            "Seats/SEATS_02_Negative_Testing.txt": "43b78c7ad1a318ab9a2aa259ed4e74c1ce8615fec9aa043079a53ed684794ca8",
            "Seats/SEATS.txt": "3f7fd3eb9d48c5b646c3ae73c7b982f419c2c86476370bf9c8ffbddc2a19fccf",
        }
        samples = PROJECT / "docs" / "sample-docs"
        # Samples are grouped in one folder per feed route, named like the exchange folders.
        self.assertEqual({p.name for p in samples.iterdir()}, set(FEEDS))
        self.assertTrue(all(p.is_dir() for p in samples.iterdir()))
        self.assertEqual({p.relative_to(samples).as_posix() for p in samples.rglob("*") if p.is_file()}, set(expected))
        for name, checksum in expected.items():
            self.assertTrue((samples / name).is_file())
            self.assertEqual(hashlib.sha256((samples / name).read_bytes()).hexdigest(), checksum)


if __name__ == "__main__":
    unittest.main()
