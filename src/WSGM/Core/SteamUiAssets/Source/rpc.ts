// Answering what Steam asks.
//
// The client calls a service method and reads a transport reply, not a bare value. Two gates
// answer such calls — the SteamOS Manager's GetState and the Bluetooth service's stubs — and both
// had built the same reply shape and the same query invalidation by hand.
//
// Overlaying the method itself is an ownership claim (claimMember); what is here is the rest of
// the job, which is the half that is easy to forget.

// The shape Steam reads back from a service call. BSuccess decides whether the caller proceeds at
// all, so a reply that omits it is discarded before its body is ever looked at; Body().toObject()
// is what the store then consumes.
const transportReply = (body: object) => ({
  BSuccess: () => true,
  BFailed: () => false,
  GetEResult: () => 1,
  Body: () => ({ ...body, toObject: () => body }),
});

// Replacing a stub is only half the job: react-query still holds the answer the stub gave, so the
// UI keeps rendering the refusal until the query that cached it is invalidated. Live-verified that
// the query client's invalidateQueries is reachable at module 21371.
//
// Failure is swallowed on purpose. A client whose query layer moved keeps the stale answer and the
// row simply does not update — which is a degraded surface, not a broken one, and never a reason to
// tear down a gate that is otherwise working.
const invalidateQuery = (req: ((id: string) => any) | null | undefined, queryKey: unknown) => {
  try {
    req?.("21371")?.L?.invalidateQueries({ queryKey });
  } catch {
    // Intentionally ignored; see above.
  }
};
