using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class WebSocketTransportClientTests
    {
        private const string CandidateBuilderMethodName = "BuildConnectionCandidateUris";
        private const string ReconnectDelayMethodName = "GetReconnectDelay";
        private const string WebSocketTransportClientTypeName = "MCPForUnity.Editor.Services.Transport.Transports.WebSocketTransportClient";
        private static readonly MethodInfo BuildConnectionCandidateUrisMethod = ResolveCandidateBuilderMethod();

        [Test]
        public void BuildConnectionCandidateUris_NullEndpoint_ReturnsEmptyList()
        {
            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(null);

            // Assert
            Assert.IsNotNull(candidates);
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void BuildConnectionCandidateUris_NonLocalhost_ReturnsOriginalOnly()
        {
            // Arrange
            var endpoint = new Uri("ws://127.0.0.1:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(endpoint, candidates[0]);
        }

        [Test]
        public void BuildConnectionCandidateUris_Localhost_AddsIPv4AndIPv6Fallbacks()
        {
            // Arrange
            var endpoint = new Uri("ws://localhost:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            CollectionAssert.AreEqual(
                new[] { "localhost", "127.0.0.1", "::1" },
                candidates.Select(uri => NormalizeHostForComparison(uri.Host)).ToArray());

            int uniqueCount = candidates
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.AreEqual(candidates.Count, uniqueCount, "Fallback list should not contain duplicate endpoints.");
        }

        [Test]
        public void BuildConnectionCandidateUris_LocalhostFallbacks_PreserveSchemePortPathAndQuery()
        {
            // Arrange
            var endpoint = new Uri("wss://localhost:9443/custom/path?mode=test");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            foreach (Uri candidate in candidates)
            {
                Assert.AreEqual("wss", candidate.Scheme);
                Assert.AreEqual(9443, candidate.Port);
                Assert.AreEqual("/custom/path", candidate.AbsolutePath);
                Assert.AreEqual("?mode=test", candidate.Query);
            }
        }

        [Test]
        public void ConnectionLifecycle_UsesGenerationContextAndOneSupervisor()
        {
            Type clientType = typeof(WebSocketTransportClient);
            Type contextType = clientType.GetNestedType(
                "ConnectionContext",
                BindingFlags.NonPublic);

            Assert.IsNotNull(contextType);
            Assert.IsNotNull(contextType.GetProperty("Generation"));
            Assert.IsNotNull(contextType.GetProperty("ConnectionId"));
            Assert.IsNotNull(contextType.GetProperty("Ready"));
            Assert.IsNotNull(contextType.GetProperty("Disconnected"));
            Assert.IsNotNull(clientType.GetMethod(
                "SuperviseConnectionsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsNull(clientType.GetMethod(
                "AttemptReconnectAsync",
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsNull(clientType.GetField(
                "_keepAliveTask",
                BindingFlags.Instance | BindingFlags.NonPublic));
        }

        [Test]
        public void GetReconnectDelay_AfterSchedule_UsesBoundedTail()
        {
            MethodInfo method = typeof(WebSocketTransportClient).GetMethod(
                ReconnectDelayMethodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            TimeSpan initial = (TimeSpan)method.Invoke(null, new object[] { 0 });
            TimeSpan tail = (TimeSpan)method.Invoke(null, new object[] { int.MaxValue });

            Assert.AreEqual(TimeSpan.Zero, initial);
            Assert.AreEqual(TimeSpan.FromSeconds(30), tail);
        }

        [Test]
        public void IsExpectedRemoteClose_ClassifiesNormalLifecycleCodesOnly()
        {
            MethodInfo method = typeof(WebSocketTransportClient).GetMethod(
                "IsExpectedRemoteClose",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            Assert.IsTrue((bool)method.Invoke(
                null,
                new object[] { WebSocketCloseStatus.NormalClosure }));
            Assert.IsTrue((bool)method.Invoke(
                null,
                new object[] { WebSocketCloseStatus.EndpointUnavailable }));
            Assert.IsFalse((bool)method.Invoke(
                null,
                new object[] { WebSocketCloseStatus.InternalServerError }));
            Assert.IsFalse((bool)method.Invoke(
                null,
                new object[] { null }));
        }

        private static List<Uri> InvokeBuildConnectionCandidateUris(Uri endpoint)
        {
            if (BuildConnectionCandidateUrisMethod == null)
            {
                Assert.Fail(BuildMissingMethodDiagnostic());
            }
            var result = BuildConnectionCandidateUrisMethod.Invoke(null, new object[] { endpoint });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<List<Uri>>(result);
            return (List<Uri>)result;
        }

        private static MethodInfo ResolveCandidateBuilderMethod()
        {
            MethodInfo direct = GetCandidateBuilderMethod(typeof(WebSocketTransportClient));
            if (direct != null)
            {
                return direct;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                MethodInfo method = GetCandidateBuilderMethod(candidateType);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo GetCandidateBuilderMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            MethodInfo direct = type.GetMethod(
                CandidateBuilderMethodName,
                flags,
                binder: null,
                types: new[] { typeof(Uri) },
                modifiers: null);
            if (direct != null)
            {
                return direct;
            }

            // Fallback for environments where signature binding can differ between loaded copies.
            return type.GetMethods(flags).FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, CandidateBuilderMethodName, StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Uri);
            });
        }

        private static string BuildMissingMethodDiagnostic()
        {
            var sb = new StringBuilder();
            sb.Append("Expected private candidate builder method to exist. Searched loaded assemblies for ")
              .Append(WebSocketTransportClientTypeName)
              .Append('.')
              .Append(CandidateBuilderMethodName)
              .Append(". Loaded candidate types:");

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                sb.Append("\n- ")
                  .Append(assembly.FullName)
                  .Append(" @ ")
                  .Append(string.IsNullOrEmpty(assembly.Location) ? "<dynamic>" : assembly.Location);
            }

            return sb.ToString();
        }

        private static string NormalizeHostForComparison(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            return host.Trim('[', ']');
        }
    }
}
