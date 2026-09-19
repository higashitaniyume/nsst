using System.Text.Json;
using System.Text.Json.Serialization;
using Nsst.Server.Http;

namespace Nsst.Server.Api;

/// <summary>
/// The serializer context covering every REST body this service produces.
/// </summary>
/// <remarks>
/// Source-generated rather than reflection-based, for two reasons: it keeps reflection off the
/// request path, and it turns the set of serialised types into an explicit, reviewable list
/// instead of "whatever happened to be reachable at runtime".
///
/// There is no global <c>DefaultIgnoreCondition</c> here on purpose. The handful of fields that
/// must vanish when absent — no <c>referer</c>, no forwarding chain, no TLS version — carry
/// <see cref="JsonIgnoreAttribute"/> individually, because "the client did not send this" and
/// "the value is nothing" are different statements and the distinction belongs on the property
/// rather than in an ambient option a future type would silently inherit.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LimitsView))]
[JsonSerializable(typeof(DefaultsView))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(MetricsView))]
[JsonSerializable(typeof(ClientResponse))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
