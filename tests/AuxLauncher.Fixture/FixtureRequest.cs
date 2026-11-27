internal sealed record FixtureRequest(string Id,string Kind,string Host="198.18.0.42",int Port=0,string Payload="aux-launcher-test",string? Directory=null,int TimeoutMs=5000);

