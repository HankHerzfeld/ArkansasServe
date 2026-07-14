namespace ArkansasServe.Functions.Functions;

// The Terms & Privacy version a person must have accepted to be considered current.
//
// Acceptance is recorded as a VERSION, not a boolean, so that changing the documents
// re-prompts everyone: /users/me reports needsPolicyAcceptance whenever a person's
// recorded version differs from Current. Bump this the moment the wording changes in a
// way people should see again — in particular when counsel signs off and the drafts stop
// being drafts.
//
// Keep this string identical to the one rendered in frontend/terms.html and
// frontend/privacy.html (`id="policy-version"`), or people will be asked to accept a
// version whose text they cannot read.
public static class PolicyVersions
{
	public const string Current = "2026-07-14-draft";

	// The client proposes a version; the server decides whether it is the one in force.
	// Accepting an arbitrary client-supplied string would let a stale tab (or a crafted
	// request) record consent to wording that was never shown.
	public static bool IsCurrent(string? version) =>
		string.Equals(version?.Trim(), Current, StringComparison.Ordinal);
}
