namespace Perch;

/// Wire format: one UDP datagram to 127.0.0.1:Port, UTF-8, fields joined by Sep:
///   nb1 | type(T/A/N) | key | state | label | cwd | pid | extra
static class Proto
{
    public const int Port = 47811;
    public const char Sep = '\u001f';
}
