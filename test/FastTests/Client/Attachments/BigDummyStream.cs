using System;
using System.IO;
using Xunit;

namespace FastTests.Client.Attachments
{
    public class BigDummyStream : Stream
    {
        private byte _counter;
        private long _size;

        public BigDummyStream(long size)
        {
            _size = size;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (int i = offset; i < count; i++)
            {
                if (_size-- <= 0)
                    return i - offset;

                buffer[i] = _counter++;
                Position++;
            }
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = offset; i < count; i++)
            {
                if (_size-- <= 0)
                    return;

                Assert.Equal(buffer[i], _counter++);
                Position++;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override bool CanRead { get; } = true;
        public override bool CanSeek { get; } = true;
        public override bool CanWrite { get; } = true;
        public override long Length => _size;
        public override long Position { get; set; }
    }
}