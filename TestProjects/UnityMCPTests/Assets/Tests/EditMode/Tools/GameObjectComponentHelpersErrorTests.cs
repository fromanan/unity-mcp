using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Tools.GameObjects;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Tests for GameObjectComponentHelpers.SetComponentPropertiesInternal error reporting.
    /// Reproduces issue #765: conversion failures incorrectly reported as "Property not found".
    /// </summary>
    public class GameObjectComponentHelpersErrorTests
    {
        private GameObject testGo;

        [SetUp]
        public void SetUp()
        {
            testGo = new GameObject("ErrorTestGO");
            CommandRegistry.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            if (testGo != null)
                Object.DestroyImmediate(testGo);
        }

        /// <summary>
        /// When a property exists but conversion fails, the error should say
        /// "Failed to convert" rather than "Property not found. Did you mean: X?"
        /// </summary>
        [Test]
        public void SetComponentProperties_ConversionFailure_ReportsConversionError_NotPropertyNotFound()
        {
            // Expect conversion error log from PropertyConversion (ComponentOps reflection attempt)
            LogAssert.Expect(LogType.Error, new Regex("Error converting token"));
            // Expect the warning log from SetComponentPropertiesInternal
            LogAssert.Expect(LogType.Warning, new Regex("Failed to set"));

            var audioSource = testGo.AddComponent<AudioSource>();

            // spatialBlend is a float property — passing an array triggers conversion failure
            var props = new JObject { ["spatialBlend"] = JArray.Parse("[1, 2, 3]") };

            var result = GameObjectComponentHelpers.SetComponentPropertiesInternal(
                testGo, "AudioSource", props, audioSource);

            Assert.IsNotNull(result, "Should return an error response");
            Assert.IsInstanceOf<ErrorResponse>(result);

            var errorResponse = (ErrorResponse)result;

            // The error message must NOT say "not found" for a property that exists
            Assert.IsFalse(
                errorResponse.Error.Contains("not found"),
                $"Error should report conversion failure, not 'not found'. Got: {errorResponse.Error}");
        }

        /// <summary>
        /// When a property genuinely doesn't exist, the error should still say "not found" with suggestions.
        /// </summary>
        [Test]
        public void SetComponentProperties_NonexistentProperty_ReportsNotFound()
        {
            // Expect the "not found" warning
            LogAssert.Expect(LogType.Warning, new Regex("not found"));

            var audioSource = testGo.AddComponent<AudioSource>();

            var props = new JObject { ["totallyFakeProperty"] = 42 };

            var result = GameObjectComponentHelpers.SetComponentPropertiesInternal(
                testGo, "AudioSource", props, audioSource);

            Assert.IsNotNull(result);
            Assert.IsInstanceOf<ErrorResponse>(result);

            var errorResponse = (ErrorResponse)result;

            Assert.IsTrue(
                errorResponse.Error.Contains("not found") || errorResponse.Error.Contains("failed"),
                $"Error for nonexistent property should say 'not found'. Got: {errorResponse.Error}");
        }

        /// <summary>
        /// Valid property setting should still succeed.
        /// </summary>
        [Test]
        public void SetComponentProperties_ValidProperty_Succeeds()
        {
            var audioSource = testGo.AddComponent<AudioSource>();

            var props = new JObject { ["volume"] = 0.42f };

            var result = GameObjectComponentHelpers.SetComponentPropertiesInternal(
                testGo, "AudioSource", props, audioSource);

            Assert.IsNull(result, "Should return null on success (no errors)");
            Assert.AreEqual(0.42f, audioSource.volume, 0.001f);
        }
    }
}

namespace MCPForUnityTests.Editor.Tools
{
    [TestFixture]
    public class FindGameObjectsTypeResolutionErrorTests
    {
        [Test]
        public void AmbiguousComponentName_ReturnsStructuredErrorAfterQualifiedLookup()
        {
            string qualifiedName = typeof(TypeResolutionFixtures.One.McpAmbiguousLookupProbe).FullName;
            bool qualifiedResolved = UnityTypeResolver.TryResolveDetailed(
                qualifiedName,
                out System.Type resolvedType,
                out UnityTypeResolver.ResolutionFailure qualifiedFailure,
                typeof(Component));

            Assert.IsTrue(qualifiedResolved);
            Assert.AreEqual(typeof(TypeResolutionFixtures.One.McpAmbiguousLookupProbe), resolvedType);
            Assert.IsNull(qualifiedFailure);
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Component type resolution failed \\(ambiguous_component_type\\)"));

            object result = FindGameObjects.HandleCommand(new JObject
            {
                ["searchMethod"] = "by_component",
                ["searchTerm"] = nameof(TypeResolutionFixtures.One.McpAmbiguousLookupProbe)
            });
            JObject response = JObject.FromObject(result);
            JObject data = (JObject)response["data"];
            string[] candidates = data["candidates"].ToObject<string[]>();

            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("ambiguous_component_type", response.Value<string>("code"));
            Assert.That(response.Value<string>("message"), Does.Contain("Ambiguous type reference"));
            Assert.That(response.Value<string>("hint"), Does.Contain("fully-qualified"));
            Assert.GreaterOrEqual(data.Value<int>("candidateCount"), 2);
            CollectionAssert.Contains(
                candidates,
                typeof(TypeResolutionFixtures.One.McpAmbiguousLookupProbe).FullName);
            CollectionAssert.Contains(
                candidates,
                typeof(TypeResolutionFixtures.Two.McpAmbiguousLookupProbe).FullName);
        }

        [Test]
        public void MissingComponentName_ReturnsStructuredNotFoundError()
        {
            const string missingTypeName = "McpDefinitelyMissingComponentType";
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Component type resolution failed \\(component_type_not_found\\)"));

            object result = FindGameObjects.HandleCommand(new JObject
            {
                ["searchMethod"] = "by_component",
                ["searchTerm"] = missingTypeName
            });
            JObject response = JObject.FromObject(result);
            JObject data = (JObject)response["data"];
            JArray candidates = (JArray)data["candidates"];

            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("component_type_not_found", response.Value<string>("code"));
            Assert.That(response.Value<string>("message"), Does.Contain("not found"));
            Assert.That(response.Value<string>("hint"), Does.Contain("compiled successfully"));
            Assert.AreEqual(0, data.Value<int>("candidateCount"));
            Assert.AreEqual(0, candidates.Count);
        }
    }
}

namespace MCPForUnityTests.Editor.Tools.TypeResolutionFixtures.One
{
    public sealed class McpAmbiguousLookupProbe : MonoBehaviour
    {
    }
}

namespace MCPForUnityTests.Editor.Tools.TypeResolutionFixtures.Two
{
    public sealed class McpAmbiguousLookupProbe : MonoBehaviour
    {
    }
}
