// One test at a time: the stack may be shared (the web vault's live suite runs against the same API),
// and the server answers 503 when its queue of password hashes is full. A 503 there is the server
// protecting itself, not something these tests are about.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
