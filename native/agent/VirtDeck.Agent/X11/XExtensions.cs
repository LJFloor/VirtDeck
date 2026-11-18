using System.Buffers.Binary;
using System.Text;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The four extensions the agent uses, found by name and then made to say their version.
    ///
    /// <para><b>The version handshake is not optional.</b> An extension tracks the version each
    /// client asked for and answers BadRequest to everything else until it has been asked. Skipping
    /// it is what made a <c>DamageCreate</c> come back as BadRequest during the spike.</para>
    /// </summary>
    internal sealed class XExtensions
    {
        public required Extension Damage { get; init; }
        public required Extension Fixes { get; init; }
        public required Extension Test { get; init; }
        public required Extension Randr { get; init; }

        /// <summary>The RANDR minor version the server agreed to: 3 or more has GetScreenResourcesCurrent.</summary>
        public int RandrMinor { get; init; }

        public static XExtensions Query(XConnection x)
        {
            var damage = Find(x, "DAMAGE");
            var fixes = Find(x, "XFIXES");
            var test = Find(x, "XTEST");
            var randr = Find(x, "RANDR");

            // DAMAGE and XFIXES take a 32 bit major and minor; XTEST takes a byte major and a 16 bit
            // minor. RANDR is like DAMAGE. Each is asked for the lowest version that has what we use.
            if (damage.Present) QueryVersion32(x, damage, 1, 1);
            if (fixes.Present) QueryVersion32(x, fixes, 1, 0);   // 1.0 has SelectCursorInput and GetCursorImage
            // RANDR 1.3 for GetScreenResourcesCurrent, which reads the monitors' configuration without
            // probing them. The answer is the lower of that and what the server has.
            int randrMinor = randr.Present ? (int)QueryVersion32(x, randr, 1, 3).U32(12) : 0;
            if (test.Present) TestGetVersion(x, test);

            return new XExtensions { Damage = damage, Fixes = fixes, Test = test, Randr = randr, RandrMinor = randrMinor };
        }

        private static Extension Find(XConnection x, string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            var request = new byte[8 + XConnection.Pad(bytes.Length)];
            request[0] = 98; // QueryExtension
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), (ushort)bytes.Length);
            bytes.CopyTo(request.AsSpan(8));

            var reply = x.Request(request);
            return new Extension(name, reply.U8(8) != 0, reply.U8(9), reply.U8(10));
        }

        private static XReply QueryVersion32(XConnection x, Extension extension, uint major, uint minor)
        {
            Span<byte> request = stackalloc byte[12];
            request[0] = extension.Major;
            request[1] = 0; // QueryVersion is minor opcode 0 in all three
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], major);
            BinaryPrimitives.WriteUInt32LittleEndian(request[8..], minor);
            return x.Request(request);
        }

        private static void TestGetVersion(XConnection x, Extension extension)
        {
            Span<byte> request = stackalloc byte[8];
            request[0] = extension.Major;
            request[1] = 0; // XTestGetVersion
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            request[4] = 2; // client major
            BinaryPrimitives.WriteUInt16LittleEndian(request[6..], 2); // client minor
            x.Request(request);
        }

        /// <summary>One extension: whether it is there, its request opcode and where its events start.</summary>
        internal readonly record struct Extension(string Name, bool Present, byte Major, byte FirstEvent);
    }
}
