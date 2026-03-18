# ForzaDataOut

ForzaDataOut is a .NET console service that listens for Forza telemetry via UDP, decodes the packet data, and outputs structured JSON. It can write telemetry to the console, capture it to a JSON file, and broadcast each JSON payload over a TCP listener so other tools can consume the live stream.