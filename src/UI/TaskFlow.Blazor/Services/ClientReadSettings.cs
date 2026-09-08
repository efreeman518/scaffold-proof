namespace TaskFlow.Blazor.Services;

/// <summary>
/// D-054: whether the read call sites in this host go over gRPC or REST. Resolved once at startup from
/// <c>Clients:UseGrpcReads</c>, defaulting to on when a gRPC address is available and off when it is not,
/// so a lane that does not publish the Api's second port keeps working with no configuration at all.
///
/// A concrete record injected at the call sites, deliberately not an <c>IDashboardReads</c>-style
/// abstraction: the point of the proof is that the two transports answer the same question, and burying
/// the choice behind an interface would hide exactly the thing worth seeing.
/// </summary>
/// <param name="UseGrpcReads">True when summary and metadata reads go through the gRPC client.</param>
public sealed record ClientReadSettings(bool UseGrpcReads);
