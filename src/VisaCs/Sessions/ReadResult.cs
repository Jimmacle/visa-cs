namespace VisaCs;

/// <summary>Result of a raw read (viRead): how many bytes were read and why the read stopped.</summary>
/// <param name="Count">Number of bytes read into the buffer.</param>
/// <param name="Status">Why the read completed: END, the termination character, or a full buffer.</param>
public readonly record struct ReadResult(int Count, ReadStatus Status);
