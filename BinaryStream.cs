/** 
 * ZIO.BinaryStream
 * 
 * Processes a stream of bytes into other primitive types (ints, floats, etc).  This stream serves
 * to simplify the process of reading and writing data to a file.  This stream conforms to numerous
 * IEEE standards and is proven to work using numerous different sources.  The stream can be used
 * both with files and using a TCP or similar stream.
 * 
 * Example:
 *   BinaryStream stream = new BinaryStream();
 *   stream.Write<float>(3.14159f);
 *   stream.Flush();
 *   stream.Reset();
 *   stream.Read<float>(); // = 3.14159f
 * 
 * VERSION 1.0:
 *   - Initial version
 *   - Added support for Endianness
 * VERSION 1.1:
 *   - Added support for generic methods such as Read<> and Write<>
 *   - Added FromFile()
 *   - Fixed known issue with IEEE-754 handling in both Read<> and Write<> methods
 * VERSION 1.2:
 *   - Added array support
 *   - Added byte array support
 *   - Added ToByteArray()
 *   - Added implicit conversion BinaryStream -> byte[]
 *   - Added explicit conversion byte[] -> BinaryStream
 * VERSION 1.3:
 *   - Added cascading support (Read<int, float, int, short, long>())
 *   - Added property Available (how many bytes are left in the stream)
 *   - Added property MarkSupported
 *   - Added methods Mark() and Reset()
 * VERSION 1.4:
 *   - Added += operator (can support any obvious type)
 *   - Added >> and << operator (shifts stream position right or left respectively)
 *   - Added array index operator (on supported streams, gets byte at position)
 *   - Added implicit conversion BinaryStream -> System.IO.Stream
 * VERSION 1.5:
 *   - Added the Network property
 *   - Added the CanRead property
 *   - Added the CanWrite property
 *   - Added the CanCommunicate property
 *   - Added EndOfStream event with its delegate EndOfStreamDelegate.
 *   - Added ClientDisconnect event with its delegate ClientDisconnectDelegate.
 *   - Added support for network (TCP/UDP) streams.
 *   - Centralized byte reading/writing in the Read<> and Write<> methods.
 * VERSION 1.5.1:
 *   - Refined some documentation.
 * VERSION 1.5.2:
 *   - Added the Encoding property
 *   - Added support for text encoding
 * VERSION 1.5.3:
 *   - Implemented IEnumerable
 * VERSION 1.5.4:
 *   - Added support for sequential and explicit struct layout reading and writing.
 *   - NOTE: Structure data will *ALWAYS* be written and read in big endian.
 *   - Added enum support
 * VERSION 1.6:
 *   - Minor code fixes.
 *   
 * Class created by:
 * Ian A Zimmerman <zz@zazzmatazz.com>
 * Copyright © 2013.  All Rights Reserved.
 */

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ZIO
{
    #region Enums
    public enum Endianness
    {
        Little,
        Big
    }
    #endregion
    #region Exceptions
    [Serializable]
    public class UnsupportedTypeException : Exception
    {
        public UnsupportedTypeException()
        { }
        public UnsupportedTypeException(string message) : base(message) { }
        public UnsupportedTypeException(string message, Exception inner) : base(message, inner) { }
    }
    #endregion
    #region Delegates
    public delegate void EndOfStreamDelegate();
    public delegate void ClientDisconnectDelegate();
    #endregion
    #region Classes
    /// <summary>
    /// <para>Used when a stream contains several distinct primitive
    /// types in sequence (i.e. INT-FLOAT-FLOAT-INT-LONG-ULONG-BYTE),
    /// and gives an interface to read from.</para>
    /// </summary>
    public class BinaryStream : IDisposable, IEnumerable
    {
        #region Constants
        /// <summary>
        /// Size, in bytes, of a short primitive type.
        /// </summary>
        private const int SIZEOF_SHORT = 2;
        /// <summary>
        /// Size, in bytes, of an integer primitive type.
        /// </summary>
        private const int SIZEOF_INT = 4;
        /// <summary>
        /// Size, in bytes, of a floating-point primitive type.
        /// </summary>
        private const int SIZEOF_FLOAT = 4;
        /// <summary>
        /// Size, in bytes, of a long primitive type.
        /// </summary>
        private const int SIZEOF_LONG = 8;
        /// <summary>
        /// Size, in bytes, of a double-precision primitive type.
        /// </summary>
        private const int SIZEOF_DOUBLE = 8;
        /// <summary>
        /// Bias of exponent in floating-point conversions,
        /// from IEEE-754.  True exponent is determined by the
        /// formula E - B, where E = Exponent and B = Bias.
        /// </summary>
        private const int FLOAT_BIAS = 127;
        /// <summary>
        /// Bias of exponent in double-precision conversions,
        /// from IEEE-754.  True exponent is determined by the
        /// formula E - B, where E = Exponent and B = Bias.
        /// </summary>
        private const int DOUBLE_BIAS = 1023;
        /// <summary>
        /// Used to determine whether a float has a value of 0.
        /// Floating point values tend to have very odd numbers
        /// as zero, including 5.877472E-39.
        /// </summary>
        private const float EPSILON = 1e-6f;
        #endregion
        #region Properties
        /// <summary>
        /// Current position in bytes from the beginning of the stream.
        /// </summary>
        public long Position
        {
            get
            {
                return _stream.Position;
            }

            set
            {
                Seek(value, SeekOrigin.Begin);
            }
        }

        /// <summary>
        /// Length of the string in bytes, provided the
        /// stream is not a network stream.
        /// </summary>
        /// <exception cref="NotSupportedException">When the stream is a network stream.</exception>
        public long Length
        {
            get
            {
                if (!IsNetwork)
                    return _stream.Length;
                throw new NotSupportedException("Length is not supported on a network stream.");
            }
        }

        /// <summary>
        /// How much data is available to read, if the stream
        /// is not a network stream.
        /// </summary>
        /// <exception cref="NotSupportedException">When this stream is reading from the network.</exception>
        public long Available
        {
            get
            {
                if (!IsNetwork)
                    return Length - Position;
                throw new NotSupportedException("Network streams do not support this property.");
            }
        }

        /// <summary>
        /// Determines if the stream appears to be a network
        /// type stream.  This is determined by whether the
        /// stream is an instance of System.Net.Sockets.NetworkStream
        /// or tosses a "NotSupportedException" on the Stream.Length
        /// property.  Either of these indicates a network stream.
        /// </summary>
        public bool IsNetwork { get; }

        /// <summary>
        /// Determines if the stream can be written to.
        /// </summary>
        public bool CanWrite => _stream.CanWrite;

        /// <summary>
        /// Determines if the stream can be read from.
        /// </summary>
        public bool CanRead => _stream.CanRead;

        /// <summary>
        /// Determines if the stream can be written to and read from.
        /// </summary>
        public bool CanCommunicate => (CanWrite && CanRead);

        /// <summary>
        /// Flags whether Mark() and Reset() are supported.
        /// </summary>
        public bool MarkSupported => true;

        /// <summary>
        /// Sets the encoding scheme used for reading and writing text.
        /// </summary>
        public Encoding Encoding
        {
            get;
            set;
        }
        #endregion
        #region Fields
        /// <summary>
        /// The endianness of read operations.  Endianness being
        /// the organizational structure of the array, from Little-Endian
        /// (least significant bit first) to Big-Endian (most significant
        /// bit first)
        /// </summary>
        private Endianness _endianness = Endianness.Little;

        /// <summary>
        /// Stream to read data from.
        /// </summary>
        private readonly Stream _stream;

        /// <summary>
        /// Position marked by the user for the reset() method.
        /// </summary>
        private long _markPosition;

        /// <summary>
        /// Read limit before the marked position becomes invalid.
        /// </summary>
        private long _markReadLimit = -1;
        #endregion
        #region Events
        /// <summary>
        /// Raised when the end of stream has been reached.
        /// </summary>
        public event EndOfStreamDelegate EndOfStream;
        /// <summary>
        /// Raised when the client at the other end of the network stream
        /// has disconnected or is otherwise unavailable.
        /// 
        /// Note that this event will only fire once data has been attempted to be
        /// read or written onto the stream.  It will not fire instantly after
        /// the disconnect of the client.
        /// </summary>
        public event ClientDisconnectDelegate ClientDisconnect;
        #endregion
        #region Methods
        #region BinaryStream()
        public BinaryStream()
        {
            Encoding = Encoding.Default;

            _stream = new MemoryStream();
        }
        #endregion
        #region BinaryStream(Stream)
        /// <summary>
        /// Initializes a BinaryStream instance.
        /// </summary>
        /// <param name="stream">Stream to read data from.</param>
        public BinaryStream(Stream stream)
        {
            Encoding = Encoding.Default;

            _stream = stream;

            // Detect whether this stream is over the network
            // NetworkStream always throws a NotSupportedException when
            // NetworkStream.Length is accessed.
            if (stream is NetworkStream)
                IsNetwork = true;
        }
        #endregion
        #region BinaryStream(byte[])
        public BinaryStream(byte[] data)
        {
            Encoding = Encoding.Default;
            MemoryStream stream = new MemoryStream(data);
            _stream = stream;
        }
        #endregion
        #region ~BinaryStream()
        ~BinaryStream()
        {
            Dispose();
            
        }
        #endregion
        #region Dispose()
        public void Dispose()
        {
            Dispose(true);

            // Call SuppressFinalize in case a subclass implements a finalizer
            GC.SuppressFinalize(this);
        }
        #endregion
        #region Dispose(bool)
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream?.Dispose();
            }
        }
        #endregion
        #region Equals(object)
        public override bool Equals(object obj)
        {
            BinaryStream b = obj as BinaryStream;
            if (b == null) return false;
            
            byte[] arrayA = ToByteArray();
            byte[] arrayB = b.ToByteArray();

            bool isEqual = arrayA.Length == arrayB.Length;
            for (long i = 0; i < arrayA.Length; i++)
                if (!arrayA[i].Equals(arrayB[i]))
                {
                    isEqual = false;
                    break;
                }

            return isEqual;
        }

        #endregion
        #region GetHashCode()
        public override int GetHashCode()
        {
            return _stream.GetHashCode();
        }
        #endregion
        #region ToString()
        public override string ToString()
        {
            return "";
        }
        #endregion
        #region ToByteArray()
        public byte[] ToByteArray()
        {
            if (_stream.CanSeek && _stream.CanRead)
            {
                lock (this)
                {
                    long pos = _stream.Position;
                    _stream.Seek(0, SeekOrigin.Begin);
                    byte[] toRet = new byte[_stream.Length];
                    _stream.Read(toRet, 0, toRet.Length);
                    _stream.Seek(pos, SeekOrigin.Begin);

                    return toRet;
                }
            }
            throw new InvalidOperationException("Stream does not support that operation.");
        }

        #endregion
        #region ToByteArray(long)
        public byte[] ToByteArray(long length)
        {
            if (_stream.CanSeek && _stream.CanRead)
            {
                lock (this)
                {
                    long pos = _stream.Position;
                    _stream.Seek(0, SeekOrigin.Begin);
                    byte[] toRet = new byte[(int)length];
                    _stream.Read(toRet, 0, (int)length);
                    _stream.Seek(pos, SeekOrigin.Begin);

                    return toRet;
                }
            }
            throw new InvalidOperationException("Stream does not support that operation.");
        }

        #endregion
        #region SetEndianness(Endianness)
        /// <summary>
        /// Sets the endianness of read operations.  Endianness being
        /// the organizational structure of the array, from Little-Endian
        /// (LSB first) to Big-Endian (MSB first)
        /// </summary>
        /// <param name="endianness">Value from the Endianness enumerator</param>
        public void SetEndianness(Endianness endianness)
        {
            _endianness = endianness;
        }
        #endregion
        #region Seek(long)
        /// <summary>
        /// Skips to a position from the beginning of the stream.
        /// </summary>
        /// <param name="position">Point after the beginning to start reading data.</param>
        /// <exception cref="InvalidOperationException">If a seek operation is attempted on a stream that cannot seek.</exception>
        public void Seek(long position)
        {
            Seek(position, SeekOrigin.Begin);
        }
        #endregion
        #region Seek(long, SeekOrigin)
        /// <summary>
        /// Sets the position to begin reading information
        /// after the specified origin.
        /// </summary>
        /// <param name="position">Position after origin</param>
        /// <param name="origin">Starting origin position</param>
        /// <exception cref="InvalidOperationException">If a seek operation is attempted on a stream that cannot seek.</exception>
        public void Seek(long position, SeekOrigin origin)
        {
            if (_stream.CanSeek)
            {
                if (origin == SeekOrigin.End) position = -position;

                _stream.Seek(position, origin);
            }
            else
                throw new InvalidOperationException("Cannot seek on this type of stream.");
        }
        #endregion
        #region Skip(long)
        /// <summary>
        /// Skips a specific number of bytes.
        /// </summary>
        /// <param name="numBytes">Number of bytes to skip.</param>
        /// <exception cref="InvalidOperationException">If a seek operation is attempted on a stream that cannot seek.</exception>
        public BinaryStream Skip(long numBytes)
        {
            Seek(numBytes, SeekOrigin.Current);
            return this;
        }
        #endregion
        #region Mark()
        /// <summary>
        /// Marks a position on the stream for the Reset() method.
        /// </summary>
        public void Mark()
        {
            _markPosition = Position;
            _markReadLimit = -1;
        }
        #endregion
        #region Mark(long)
        /// <summary>
        /// Marks a position on the stream for the Reset() method with a read limit.
        /// </summary>
        /// <param name="readLimit">Read limit before the marked position becomes invalid.</param>
        public void Mark(long readLimit)
        {
            _markPosition = Position;
            _markReadLimit = readLimit;
        }
        #endregion
        #region Reset()
        /// <summary>
        /// Resets position to the marked position if the mark read limit has
        /// not been exceeded or equals -1.
        /// </summary>
        public void Reset()
        {
            if (_markReadLimit == -1)
                Seek(_markPosition, SeekOrigin.Begin);

            if ((Position - _markPosition) <= _markReadLimit)
                Seek(_markPosition, SeekOrigin.Begin);
        }
        #endregion
        #region Close()
        /// <summary>
        /// Closes the stream and releases all resources (i.e.
        /// sockets or file handles) associated with the current
        /// stream.
        /// </summary>
        public void Close()
        {
            _stream.Close();
        }
        #endregion
        #region Flush()
        /// <summary>
        /// Clears all buffers for the stream and writes the
        /// buffered data to the underlying device.
        /// </summary>
        public void Flush()
        {
            _stream.Flush();
        }
        #endregion
        #region Write<T>(T)
        /// <summary>
        /// Writes an object to a stream.
        /// 
        /// Supported  types: byte, char, short, int, long, float, double, and their array counterparts, and string.
        /// </summary>
        /// <typeparam name="T">OPTIONAL:  Type of data to write.  Automatically-detected;  This will override.</typeparam>
        /// <param name="obj">Object to write to the stream.</param>
        /// <exception cref="IOException">When the stream is read-only.</exception>
        /// <exception cref="UnsupportedTypeException">When an unsupported type is passed as T.</exception>
        public void Write<T>(T obj)
        {
            Write(typeof(T), obj);
        }
        #endregion
        #region Write<T>(params T[])
        /// <summary>
        /// Writes an object or set of objects of the same type to a stream.
        /// 
        /// Supported  types: byte, char, short, int, long, float, double, and their array counterparts, and string.
        /// </summary>
        /// <typeparam name="T">OPTIONAL:  Type of data to write.  Automatically-detected;  This will override.</typeparam>
        /// <param name="objs">Object(s) to write to the stream.</param>
        /// <exception cref="IOException">When the stream is read-only.</exception>
        /// <exception cref="UnsupportedTypeException">When an unsupported type is passed as T.</exception>
        public void Write<T>(params T[] objs)
        {
            foreach (T obj in objs)
            {
                Write(typeof(T), obj);
            }
        }
        #endregion
        #region Write(Type, object)
        /// <summary>
        /// Handles all of the nasty stuff associated with generics.
        /// </summary>
        /// <param name="type">Type to write.</param>
        /// <param name="obj">Object to write.</param>
        /// <see cref="Write"/>
        private void Write(Type type, object obj)
        {
            if (!_stream.CanWrite) throw new IOException("Cannot write to the current stream.");

            #region Array
            if (type.IsArray)
            {
                Type subType = type.GetElementType();
                int len = ((Array)obj).GetLength(0);
                object[] data = new object[len];

                Array.Copy((Array)obj, data, len);

                if (_endianness == Endianness.Little)
                    Array.Reverse(data);

                foreach (object piece in data)
                    Write(subType, piece);

                return;
            }
            #endregion
            #region string
            if (type == typeof(string))
            {
                byte[] strArray = Encoding.GetBytes((string)obj);

                foreach (byte ch in strArray) Write(ch);
            }
            #endregion
            #region byte, char
            else if (type == typeof(byte))
            {
                try
                {
                    _stream.WriteByte((byte)obj);
                }
                catch (IOException)
                {
                    // User disconnected.
                    // This is handled by another method.
                    if (IsNetwork)
                    {
                        ClientDisconnect?.Invoke();
                    }
                }
            }
            else if (type == typeof(char))
            {
                _stream.WriteByte((byte)(char)obj);
            }
            #endregion
            #region short
            else if (type == typeof(short))
            {
                short num = (short)obj;
                byte[] buffer = new byte[SIZEOF_SHORT];

                // Convert number (already in correct bit form) into a byte[] array.
                for (int i = SIZEOF_SHORT - 1; i >= 0; i--)
                    buffer[SIZEOF_SHORT - i - 1] = (byte)((num & (0xFF << (i * 8))) >> (i * 8));

                // ... and write the data.
                Write<byte[]>(buffer);
            }
            else if (type == typeof(ushort))
            {
                ushort num = (ushort)obj;
                byte[] buffer = new byte[SIZEOF_SHORT];

                // Convert number (already in correct bit form) into a byte[] array.
                for (int i = SIZEOF_SHORT - 1; i >= 0; i--)
                    buffer[SIZEOF_SHORT - i - 1] = (byte)((num & (0xFF << (i * 8))) >> (i * 8));

                // ... and write the data.
                Write<byte[]>(buffer);
            }
            #endregion
            #region int
            else if (type == typeof(int))
            {
                int num = (int)obj;
                byte[] buffer = new byte[SIZEOF_INT];

                // Convert number (already in correct bit form) into a byte[] array.
                for (int i = SIZEOF_INT - 1; i >= 0; i--)
                    buffer[SIZEOF_INT - i - 1] = (byte)((num & (0xFF << (i * 8))) >> (i * 8));

                // ... and write the data.
                Write<byte[]>(buffer);
            }
            else if (type == typeof(uint))
            {
                uint num = (uint)obj;
                byte[] buffer = new byte[SIZEOF_INT];

                // Convert number (already in correct bit form) into a byte[] array.
                for (int i = SIZEOF_INT - 1; i >= 0; i--)
                    buffer[SIZEOF_INT - i - 1] = (byte)((num & (0xFF << (i * 8))) >> (i * 8));

                // ... and write the data.
                Write<byte[]>(buffer);
            }
            #endregion
            #region long
            else if (type == typeof(long))
            {
                long num = (long)obj;
                byte[] buffer = new byte[SIZEOF_LONG];

                // Convert number (already in correct bit form) into a byte[] array.
                for (int i = SIZEOF_LONG - 1; i >= 0; i--)
                    buffer[SIZEOF_LONG - i - 1] = (byte)((num & (0xFF << (i * 8))) >> (i * 8));

                // ... and write the data.
                Write<byte[]>(buffer);
            }
            else if (type == typeof(ulong))
            {
                ulong num = (ulong)obj;
                byte[] buffer = new byte[SIZEOF_LONG];

                // Convert number (already in correct bit form) into a byte[] array.
                for (int i = SIZEOF_LONG - 1; i >= 0; i--)
                    buffer[SIZEOF_LONG - i - 1] = (byte)((num & (ulong)(0xFF << (i * 8))) >> (i * 8));

                // ... and write the data.
                Write<byte[]>(buffer);
            }
            #endregion
            #region float
            else if (type == typeof(float))
            {
                // Convert obj to float.
                float num = (float)obj;

                if (num > -EPSILON && num < EPSILON)
                {
                    _stream.Write(new byte[] { 0, 0, 0, 0 }, 0, SIZEOF_FLOAT);
                    return;
                }

                // Determine the sign.
                int sign = (num > 0 ? 0 : 1);

                num = Math.Abs(num);

                // Determine exponent and normalize the mantissa.
                int exponent = 0;
                float mantissa = num;

                for (int i = -FLOAT_BIAS; i < FLOAT_BIAS; i++)
                {
                    if (i == -FLOAT_BIAS)
                        if (mantissa >= 1 && mantissa < 2)
                            break;
                        else continue;

                    float normalized = num / (float)Math.Pow(2, i);

                    if (!(normalized >= 1) || !(normalized < 2)) continue;

                    // We've found an exponent that normalizes the Mantissa.
                    exponent = i;
                    mantissa = normalized;
                    break;
                }

                // Add exponent bias
                exponent += FLOAT_BIAS;

                // Perform mantissa operations.  First, subtract one (1) to
                // remove form 1.f.
                mantissa--;
                // And denormalize the mantissa by multiplying by 2^23.
                mantissa *= 0x800000;

                // Assemble byte structure
                int bits = ((int)mantissa);
                bits ^= (exponent << 23);
                bits ^= (sign << 31);

                // Split the bits into a byte[] array.
                byte[] buffer = new byte[SIZEOF_FLOAT];
                for (int i = SIZEOF_FLOAT - 1; i >= 0; i--)
                    buffer[SIZEOF_FLOAT - i - 1] = (byte)((bits & (0xFF << (i * 8))) >> (i * 8));

                // ... and write the buffer.
                Write<byte[]>(buffer);
            }
            #endregion
            #region double
            else if (type == typeof(double))
            {
                double num = (double)obj;

                if (num > -EPSILON && num < EPSILON)
                {
                    _stream.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, 0, SIZEOF_DOUBLE);
                    return;
                }

                // Determine the sign.  This is probably the simplest
                // part of this algorithm.  Except for the above definition,
                // of course.
                int sign = (num > 0 ? 0 : 1);

                num = Math.Abs(num);

                // Determine exponent and normalize the Mantissa,
                // since to continue, the Mantissa needs to be in the form
                // 1.f
                int exponent = 0;
                double mantissa = num;
                for (int i = -DOUBLE_BIAS; i < DOUBLE_BIAS; i++)
                {
                    if (i == -DOUBLE_BIAS)
                    {
                        if (mantissa >= 1 && mantissa < 2)
                        {
                            exponent = 0;
                            break;
                        }
                        continue;
                    }

                    double normalized = num / Math.Pow(2, i);

                    if (!(normalized >= 1) || !(normalized < 2)) continue;
                    // We've found an exponent that normalizes the Mantissa.
                    exponent = i;
                    mantissa = normalized;
                }

                // Add exponent bias
                exponent += DOUBLE_BIAS;

                // Perform mantissa operations.  Subtract initial 1
                // from form 1.f
                mantissa--;
                // And denormalize by multiplying by 2^52.
                // Step also converts type into unsigned long in preparation
                // for next bitwise operations.
                ulong modifiedMantissa = (ulong)(mantissa * 0x10000000000000UL);

                // Assemble byte structure
                ulong bits = modifiedMantissa & 0xFFFFFFFFFFFFF;
                bits ^= (ulong)exponent << 52;
                bits ^= (ulong)sign << 63;

                // Split bits into a byte[] array.
                byte[] buffer = new byte[SIZEOF_DOUBLE];
                for (int i = SIZEOF_DOUBLE - 1; i >= 0; i--)
                {
                    ulong operand = (ulong)0xFF << (i * 8);
                    byte f = (byte)((bits & operand) >> (i * 8));

                    buffer[SIZEOF_DOUBLE - i - 1] = f;
                }

                // ... and write the buffer.
                Write<byte[]>(buffer);
            }
            #endregion
            #region struct
            else if (type.IsValueType && !type.IsEnum && !type.IsPrimitive)
            {
                if (type.Attributes.HasFlag(TypeAttributes.SequentialLayout) || type.Attributes.HasFlag(TypeAttributes.ExplicitLayout))
                {
                    FieldInfo[] mis = type.GetFields();
                    foreach (FieldInfo mi in mis)
                    {
                        if (mi.FieldType.IsArray)
                        {
                            Attribute[] attrs = (Attribute[])mi.GetCustomAttributes(typeof(MarshalAsAttribute), false);
                            if (attrs.Length == 0)
                                throw new InvalidDataException("The field '" + mi.Name + "' must have a MarshalAs attribute.");
                            if (((MarshalAsAttribute)attrs[0]).SizeConst == 0)
                                throw new InvalidDataException("The MarshalAs attribute on field '" + mi.Name + "' must have a SizeConst greater than zero.");

                            Array rez = (Array) mi.GetValue(obj);
                            if (((MarshalAsAttribute)attrs[0]).SizeConst != rez.Length)
                                Debug.WriteLine("[ZIO]: Field '" + mi.Name + "' is an array with " + rez.Length + " members, but the MarshalAs SizeConst value is " + ((MarshalAsAttribute)attrs[0]).SizeConst + ".  Is this intentional?");
                        }
                    }

                    int size = Marshal.SizeOf(type);
                    byte[] arr = new byte[size];
                    IntPtr ptr = Marshal.AllocHGlobal(size);

                    Marshal.StructureToPtr(obj, ptr, false);
                    Marshal.Copy(ptr, arr, 0, size);
                    Marshal.FreeHGlobal(ptr);

                    // ... and write the buffer.
                    Endianness prevEndianness = _endianness;
                    _endianness = Endianness.Big;
                    Write<byte[]>(arr);
                    _endianness = prevEndianness;
                }
            }
            #endregion
            #region enum
            else if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                object r = Convert.ChangeType(obj, underlying);

                Write(underlying, r);
            }
            #endregion
            #region Unsupported Type
            else
            {
                Type objType = obj.GetType();
                if (objType != type)
                {
                    Write(objType, obj);
                    return;
                }

                throw new UnsupportedTypeException($"Type '{type.FullName}' is not supported by this operation.");
            }
            #endregion
        }
        #endregion
        #region Read<T>(int)
        /// <summary>
        /// Reads a data type from a stream.
        /// </summary>
        /// <typeparam name="T">Type of data to read.</typeparam>
        /// <param name="length">OPTIONAL:  Length of string</param>
        /// <returns>Converted data type.</returns>
        public T Read<T>(int? length = 0)
        {
            T type = (T)Read(typeof(T), length ?? 0);
            return type;
        }
        #endregion
        #region Read<T,...>(int)
        public object[] Read<T1, T2>(int? t1Length = null, int? t2Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0) };
        }
        public object[] Read<T1, T2, T3>(int? t1Length = null, int? t2Length = null, int? t3Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4, T5>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null, int? t5Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0), Read<T5>(t5Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4, T5, T6>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null, int? t5Length = null, int? t6Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0), Read<T5>(t5Length ?? 0), Read<T6>(t6Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4, T5, T6, T7>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null, int? t5Length = null, int? t6Length = null, int? t7Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0), Read<T5>(t5Length ?? 0), Read<T6>(t6Length ?? 0), Read<T7>(t7Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4, T5, T6, T7, T8>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null, int? t5Length = null, int? t6Length = null, int? t7Length = null, int? t8Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0), Read<T5>(t5Length ?? 0), Read<T6>(t6Length ?? 0), Read<T7>(t7Length ?? 0), Read<T8>(t8Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4, T5, T6, T7, T8, T9>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null, int? t5Length = null, int? t6Length = null, int? t7Length = null, int? t8Length = null, int? t9Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0), Read<T5>(t5Length ?? 0), Read<T6>(t6Length ?? 0), Read<T7>(t7Length ?? 0), Read<T8>(t8Length ?? 0), Read<T9>(t9Length ?? 0) };
        }
        public object[] Read<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(int? t1Length = null, int? t2Length = null, int? t3Length = null, int? t4Length = null, int? t5Length = null, int? t6Length = null, int? t7Length = null, int? t8Length = null, int? t9Length = null, int? t10Length = null)
        {
            return new object[] { Read<T1>(t1Length ?? 0), Read<T2>(t2Length ?? 0), Read<T3>(t3Length ?? 0), Read<T4>(t4Length ?? 0), Read<T5>(t5Length ?? 0), Read<T6>(t6Length ?? 0), Read<T7>(t7Length ?? 0), Read<T8>(t8Length ?? 0), Read<T9>(t9Length ?? 0), Read<T10>(t10Length ?? 0) };
        }
        #endregion
        #region Read(Type, int)
        /// <summary>
        /// Handles all of the nasty stuff associated with generics.
        /// </summary>
        /// <param name="type">Type to read.</param>
        /// <param name="length">Length of array or string, if applicable.</param>
        /// <returns>Read object.</returns>
        private object Read(Type type, int length = 0)
        {
            if (!_stream.CanRead) throw new IOException("Stream is write-only.");

            #region Array
            if (type.IsArray)
            {
                // Oh boy, this will be fun.
                Type subType = type.GetElementType();

                Array array = Array.CreateInstance(subType, length);
                for (int i = 0; i < length; i++)
                {
                    array.SetValue(Read(subType), i);
                }

                if (_endianness == Endianness.Little)
                    Array.Reverse(array);

                return array;
            }
            #endregion
            #region string
            if (type == typeof(string))
            {
                byte[] buff = new byte[length];
                for (int i = 0; i < length; i++)
                    buff[i] = Read<byte>();

                return Encoding.GetString(buff);
            }
            #endregion
            #region byte, char
            if (type == typeof(byte))
            {
                try
                {
                    int d = _stream.ReadByte();

                    if (d == -1)
                    {
                        EndOfStream?.Invoke();
                    }

                    return (byte)d;
                }
                catch (IOException ioe)
                {
                    // Throw it to the next catcher
                    throw new IOException("Failed to read data from the stream.", ioe);
                }
            }
            if (type == typeof(char))
            {
                return (char)Read<byte>();
            }
                #endregion
                #region short
            if (type == typeof(short))
            {
                byte[] buffer = Read<byte[]>(SIZEOF_SHORT);

                short result = 0;
                for (int i = SIZEOF_SHORT - 1; i >= 0; i--)
                    result += (short)((buffer[SIZEOF_SHORT - i - 1] & 0xFF) << (i * 8));

                return result;
            }
            if (type == typeof(ushort))
            {
                byte[] buffer = Read<byte[]>(SIZEOF_SHORT);

                ushort result = 0;
                for (int i = SIZEOF_SHORT - 1; i >= 0; i--)
                    result += (ushort)((buffer[SIZEOF_SHORT - i - 1] & 0xFF) << (i * 8));

                return result;
            }
                #endregion
                #region int
            if (type == typeof(int))
            {
                byte[] buffer = Read<byte[]>(SIZEOF_INT);

                int result = 0;
                for (int i = SIZEOF_INT - 1; i >= 0; i--)
                    result += (buffer[SIZEOF_INT - i - 1] & 0xFF) << (i * 8);

                return result;
            }
            if (type == typeof(uint))
            {
                byte[] buffer = Read<byte[]>(SIZEOF_INT);

                long result = 0;
                for (int i = SIZEOF_INT - 1; i >= 0; i--)
                    result += (buffer[SIZEOF_INT - i - 1] & 0xFF) << (i * 8);

                return (uint)result;
            }
                #endregion
                #region long
            if (type == typeof(long))
            {
                byte[] buffer = Read<byte[]>(SIZEOF_LONG);

                long result = 0;
                for (int i = SIZEOF_LONG - 1; i >= 0; i--)
                    result += (buffer[SIZEOF_LONG - i - 1] & 0xFF) << (i * 8);

                return result;
            }
            if (type == typeof(ulong))
            {
                byte[] buffer = Read<byte[]>(SIZEOF_LONG);

                long result = 0;
                for (int i = SIZEOF_LONG - 1; i >= 0; i--)
                    result += (buffer[SIZEOF_LONG - i - 1] & 0xFF) << (i * 8);

                return (ulong)result;
            }
                #endregion
                #region float
            if (type == typeof(float))
            {
                // Commencing heavy wizardry.
                // See Write(Type, object) case 'float' for clarification.
                byte[] buffer = Read<byte[]>(SIZEOF_FLOAT);
                // This algorithm is optimized for Little Endian.
                Array.Reverse(buffer);

                int bits = 0;
                for (int i = SIZEOF_INT; i > 0; i--)
                    bits += (buffer[SIZEOF_INT - i] & 0xFF) << ((SIZEOF_INT - i) * 8);
                int sign = (bits >> 31) == 0 ? 1 : -1;
                int exponent = ((bits & 0x7F800000) >> 23) - FLOAT_BIAS;
                float mantissa = bits & 0x7FFFFF;
                mantissa /= 0x800000; // 2^23
                mantissa++;
                mantissa *= (float)Math.Pow(2, exponent);
                mantissa *= sign;

                if (mantissa > -EPSILON && mantissa < EPSILON)
                    return 0.0f;

                return mantissa;
            }
                #endregion
                #region double
            if (type == typeof(double))
            {
                // More heavy wizardry.
                // See Write(Type, object) case 'double' for clarification.

                byte[] buffer = Read<byte[]>(SIZEOF_LONG);
                // This algorithm is optimized for Little Endian.
                Array.Reverse(buffer);

                long bits = 0;
                for (int i = 0; i < SIZEOF_LONG; i++)
                {
                    bits <<= 8;
                    bits ^= (long)buffer[SIZEOF_LONG - i - 1] & 0xFF;
                }
                long sign = (bits >> 63) == 0 ? 1 : -1;
                long exponent = ((bits & 0x7FF0000000000000) >> 52) - DOUBLE_BIAS;
                double mantissa = bits & 0xFFFFFFFFFFFFF;
                mantissa /= 0x10000000000000UL; // forced to unsigned long due to signing constraints
                mantissa++;
                mantissa *= (float)Math.Pow(2, exponent);
                mantissa *= sign;

                return mantissa;
            }
                #endregion
                #region struct
            if (type.IsValueType && !type.IsEnum && !type.IsPrimitive)
            {
                if (type.Attributes.HasFlag(TypeAttributes.SequentialLayout) || type.Attributes.HasFlag(TypeAttributes.ExplicitLayout))
                {
                    if (type.StructLayoutAttribute != null)
                    {
                        int size = type.StructLayoutAttribute.Size;
                        if (size == 0)
                            size = Marshal.SizeOf(type);

                        Endianness prevEndianness = _endianness;
                        _endianness = Endianness.Big;
                        byte[] buffer = Read<byte[]>(size);

                        try
                        {
                            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                            object rez = Marshal.PtrToStructure(handle.AddrOfPinnedObject(), type);

                            handle.Free();

                            _endianness = prevEndianness;

                            return rez;
                        }
                        catch
                        {
                            return null;
                        }
                    }
                }
                return null;
            }
                #endregion
                #region enum
            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                object r = Read(underlying);

                return Enum.ToObject(type, r);
            }

            #endregion

            return null;
        }
        #endregion
        #region FromFile(string)
        /// <summary>
        /// Acquires a BinaryStream from a file path.
        /// </summary>
        /// <param name="path">Path to the file to be opened</param>
        /// <returns>BinaryStream handle for the opened file</returns>
        public static BinaryStream FromFile(string path)
        {
            if (File.Exists(path))
            {
                FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return new BinaryStream(stream);
            }
            throw new IOException("File not found: " + path);
        }

        #endregion
        #region GetEnumerator()
        /// <summary>
        /// Returns an enumerator that iterates through all bytes in the stream.
        /// </summary>
        /// <returns>An System.Collections.IEnumerator object that can be used to iterate through all bytes in the stream.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return ToByteArray().GetEnumerator();
        }
        #endregion
        #endregion
        #region Implicit Casting Operators
        #region implicit Stream
        public static implicit operator Stream(BinaryStream strm)
        {
            return strm._stream;
        }
        #endregion
        #region implicit byte[]
        public static implicit operator byte[](BinaryStream strm)
        {
            return strm.ToByteArray();
        }
        #endregion
        #endregion
        #region Explicit Casting Operators
        public static explicit operator BinaryStream(byte[] data)
        {
            return new BinaryStream(data);
        }
        #endregion
        #region Operators
        public static BinaryStream operator +(BinaryStream a, string b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, byte b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, char b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, short b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, int b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, long b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, float b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, double b)
        {
            a.Write(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, string[] b)
        {
            a.Write<string[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, byte[] b)
        {
            a.Write<byte[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, char[] b)
        {
            a.Write<char[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, short[] b)
        {
            a.Write<short[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, int[] b)
        {
            a.Write<int[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, long[] b)
        {
            a.Write<long[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, float[] b)
        {
            a.Write<float[]>(b);
            return a;
        }
        public static BinaryStream operator +(BinaryStream a, double[] b)
        {
            a.Write<double[]>(b);
            return a;
        }

        /// <summary>
        /// A very simple method for removing data from the top of the stack.
        /// 
        /// DANGER: This method will close any underlying streams.  It is
        /// intended to be used with Binary Streams created with
        /// new BinaryStream(byte[]) or new BinaryStream() ONLY.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static BinaryStream operator -(BinaryStream a, Type b)
        {
            byte[] buff = a.ToByteArray(a.Length - Marshal.SizeOf(b));

            BinaryStream c = new BinaryStream(buff);
            c.Seek(a.Position - Marshal.SizeOf(b));

            return c;
        }

        public static BinaryStream operator ++(BinaryStream a)
        {
            if ((a.Position + 1) <= a.Length)
                a.Skip(1);
            return a;
        }
        public static BinaryStream operator --(BinaryStream a)
        {
            if ((a.Position - 1) >= 0)
                a.Skip(-1);
            return a;
        }

        public static BinaryStream operator >>(BinaryStream a, int bytes)
        {
            if (bytes > 0)
            {
                if ((a.Position + bytes) <= a.Length)
                    a.Skip(bytes);
                else
                    a.Seek(a.Length);
            }
            else a = a << -bytes;

            return a;
        }
        public static BinaryStream operator <<(BinaryStream a, int bytes)
        {
            if (bytes > 0)
            {
                if ((a.Position - bytes) >= 0)
                    a.Skip(-bytes);
                else
                    a.Seek(0);
            }
            else a = a >> -bytes;

            return a;
        }

        public static bool operator ==(BinaryStream a, object b)
        {
            return a != null && a.Equals(b);
        }
        public static bool operator !=(BinaryStream a, object b)
        {
            return a != null && !a.Equals(b);
        }

        public static bool operator <(BinaryStream a, object b)
        {
            BinaryStream b2 = b as BinaryStream;
            if (b2 != null)
            {
                return a.Length < b2.Length;
            }
            return false;
        }

        public static bool operator >(BinaryStream a, object b)
        {
            BinaryStream b2 = b as BinaryStream;
            if (b2 != null)
            {
                return a.Length > b2.Length;
            }
            return false;
        }

        public static bool operator <=(BinaryStream a, object b)
        {
            BinaryStream b2 = b as BinaryStream;
            if (b2 != null)
            {
                return a.Length <= b2.Length;
            }
            return false;
        }

        public static bool operator >=(BinaryStream a, object b)
        {
            BinaryStream b2 = b as BinaryStream;
            if (b2 != null)
            {
                return a.Length >= b2.Length;
            }
            return false;
        }

        #endregion
        #region Indexing Operators
        /// <summary>
        /// Returns the byte at the position provided.
        /// If the stream is a network stream, then this method
        /// will throw a NotSupportedException.
        /// </summary>
        /// <param name="position">The zero-based position to get data from.</param>
        /// <returns>The byte at the position provided.</returns>
        /// <exception cref="NotSupportedException">If this stream is a network stream.</exception>
        public byte this[long position]
        {
            get
            {
                if (IsNetwork)
                    throw new NotSupportedException("This property is not available on network type streams.");

                if (MarkSupported)
                {
                    Mark();
                    Seek(position);
                    byte result = Read<byte>();
                    Reset();

                    return result;
                }
                throw new NotSupportedException("This property is not available due to the lack of mark support.");
            }
        }
        #endregion
    }
    #endregion
}
