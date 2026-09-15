DEVELOPMENT KEYS — NOT FOR RELEASE

This pair exists so the samples work from a fresh clone: Sample.ReleaseServer signs with
dev-private.pem and Sample.App verifies with dev-public.pem. Because the private half is public,
anyone can sign a release these samples will accept.

For a real app, create a pair with WebAppReleaseSignature.CreateKeyPair() (or openssl), keep the
private key in the server's secret store, and compile only the public key into the app.
