


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
namespace uniffi.rust_lib;



// This is a helper for safely working with byte buffers returned from the Rust code.
// A rust-owned buffer is represented by its capacity, its current length, and a
// pointer to the underlying data.

[StructLayout(LayoutKind.Sequential)]
internal struct RustBuffer {
    public int capacity;
    public int len;
    public IntPtr data;

    public static RustBuffer Alloc(int size) {
        return _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            var buffer = _UniFFILib.ffi_rust_lib_rustbuffer_alloc(size, ref status);
            if (buffer.data == IntPtr.Zero) {
                throw new AllocationException($"RustBuffer.Alloc() returned null data pointer (size={size})");
            }
            return buffer;
        });
    }

    public static void Free(RustBuffer buffer) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_rust_lib_rustbuffer_free(buffer, ref status);
        });
    }

    public static BigEndianStream MemoryStream(IntPtr data, int length) {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), length));
        }
    }

    public BigEndianStream AsStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), len));
        }
    }

    public BigEndianStream AsWriteableStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), capacity, capacity, FileAccess.Write));
        }
    }
}

// This is a helper for safely passing byte references into the rust code.
// It's not actually used at the moment, because there aren't many things that you
// can take a direct pointer to managed memory, and if we're going to copy something
// then we might as well copy it into a `RustBuffer`. But it's here for API
// completeness.

[StructLayout(LayoutKind.Sequential)]
internal struct ForeignBytes {
    public int length;
    public IntPtr data;
}


// The FfiConverter interface handles converter types to and from the FFI
//
// All implementing objects should be public to support external types.  When a
// type is external we need to import it's FfiConverter.
internal abstract class FfiConverter<CsType, FfiType> {
    // Convert an FFI type to a C# type
    public abstract CsType Lift(FfiType value);

    // Convert C# type to an FFI type
    public abstract FfiType Lower(CsType value);

    // Read a C# type from a `ByteBuffer`
    public abstract CsType Read(BigEndianStream stream);

    // Calculate bytes to allocate when creating a `RustBuffer`
    //
    // This must return at least as many bytes as the write() function will
    // write. It can return more bytes than needed, for example when writing
    // Strings we can't know the exact bytes needed until we the UTF-8
    // encoding, so we pessimistically allocate the largest size possible (3
    // bytes per codepoint).  Allocating extra bytes is not really a big deal
    // because the `RustBuffer` is short-lived.
    public abstract int AllocationSize(CsType value);

    // Write a C# type to a `ByteBuffer`
    public abstract void Write(CsType value, BigEndianStream stream);

    // Lower a value into a `RustBuffer`
    //
    // This method lowers a value into a `RustBuffer` rather than the normal
    // FfiType.  It's used by the callback interface code.  Callback interface
    // returns are always serialized into a `RustBuffer` regardless of their
    // normal FFI type.
    public RustBuffer LowerIntoRustBuffer(CsType value) {
        var rbuf = RustBuffer.Alloc(AllocationSize(value));
        try {
            var stream = rbuf.AsWriteableStream();
            Write(value, stream);
            rbuf.len = Convert.ToInt32(stream.Position);
            return rbuf;
        } catch {
            RustBuffer.Free(rbuf);
            throw;
        }
    }

    // Lift a value from a `RustBuffer`.
    //
    // This here mostly because of the symmetry with `lowerIntoRustBuffer()`.
    // It's currently only used by the `FfiConverterRustBuffer` class below.
    protected CsType LiftFromRustBuffer(RustBuffer rbuf) {
        var stream = rbuf.AsStream();
        try {
           var item = Read(stream);
           if (stream.HasRemaining()) {
               throw new InternalException("junk remaining in buffer after lifting, something is very wrong!!");
           }
           return item;
        } finally {
            RustBuffer.Free(rbuf);
        }
    }
}

// FfiConverter that uses `RustBuffer` as the FfiType
internal abstract class FfiConverterRustBuffer<CsType>: FfiConverter<CsType, RustBuffer> {
    public override CsType Lift(RustBuffer value) {
        return LiftFromRustBuffer(value);
    }
    public override RustBuffer Lower(CsType value) {
        return LowerIntoRustBuffer(value);
    }
}


// A handful of classes and functions to support the generated data structures.
// This would be a good candidate for isolating in its own ffi-support lib.
// Error runtime.
[StructLayout(LayoutKind.Sequential)]
struct RustCallStatus {
    public sbyte code;
    public RustBuffer error_buf;

    public bool IsSuccess() {
        return code == 0;
    }

    public bool IsError() {
        return code == 1;
    }

    public bool IsPanic() {
        return code == 2;
    }
}

// Base class for all uniffi exceptions
public class UniffiException: Exception {
    public UniffiException(): base() {}
    public UniffiException(string message): base(message) {}
}

public class UndeclaredErrorException: UniffiException {
    public UndeclaredErrorException(string message): base(message) {}
}

public class PanicException: UniffiException {
    public PanicException(string message): base(message) {}
}

public class AllocationException: UniffiException {
    public AllocationException(string message): base(message) {}
}

public class InternalException: UniffiException {
    public InternalException(string message): base(message) {}
}

public class InvalidEnumException: InternalException {
    public InvalidEnumException(string message): base(message) {
    }
}

public class UniffiContractVersionException: UniffiException {
    public UniffiContractVersionException(string message): base(message) {
    }
}

public class UniffiContractChecksumException: UniffiException {
    public UniffiContractChecksumException(string message): base(message) {
    }
}

// Each top-level error class has a companion object that can lift the error from the call status's rust buffer
interface CallStatusErrorHandler<E> where E: Exception {
    E Lift(RustBuffer error_buf);
}

// CallStatusErrorHandler implementation for times when we don't expect a CALL_ERROR
class NullCallStatusErrorHandler: CallStatusErrorHandler<UniffiException> {
    public static NullCallStatusErrorHandler INSTANCE = new NullCallStatusErrorHandler();

    public UniffiException Lift(RustBuffer error_buf) {
        RustBuffer.Free(error_buf);
        return new UndeclaredErrorException("library has returned an error not declared in UNIFFI interface file");
    }
}

// Helpers for calling Rust
// In practice we usually need to be synchronized to call this safely, so it doesn't
// synchronize itself
class _UniffiHelpers {
    public delegate void RustCallAction(ref RustCallStatus status);
    public delegate U RustCallFunc<out U>(ref RustCallStatus status);

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static U RustCallWithError<U, E>(CallStatusErrorHandler<E> errorHandler, RustCallFunc<U> callback)
        where E: UniffiException
    {
        var status = new RustCallStatus();
        var return_value = callback(ref status);
        if (status.IsSuccess()) {
            return return_value;
        } else if (status.IsError()) {
            throw errorHandler.Lift(status.error_buf);
        } else if (status.IsPanic()) {
            // when the rust code sees a panic, it tries to construct a rustbuffer
            // with the message.  but if that code panics, then it just sends back
            // an empty buffer.
            if (status.error_buf.len > 0) {
                throw new PanicException(FfiConverterString.INSTANCE.Lift(status.error_buf));
            } else {
                throw new PanicException("Rust panic");
            }
        } else {
            throw new InternalException($"Unknown rust call status: {status.code}");
        }
    }

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static void RustCallWithError<E>(CallStatusErrorHandler<E> errorHandler, RustCallAction callback)
        where E: UniffiException
    {
        _UniffiHelpers.RustCallWithError(errorHandler, (ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }

    // Call a rust function that returns a plain value
    public static U RustCall<U>(RustCallFunc<U> callback) {
        return _UniffiHelpers.RustCallWithError(NullCallStatusErrorHandler.INSTANCE, callback);
    }

    // Call a rust function that returns a plain value
    public static void RustCall(RustCallAction callback) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }
}


// Big endian streams are not yet available in dotnet :'(
// https://github.com/dotnet/runtime/issues/26904

class StreamUnderflowException: Exception {
    public StreamUnderflowException() {
    }
}

class BigEndianStream {
    Stream stream;
    public BigEndianStream(Stream stream) {
        this.stream = stream;
    }

    public bool HasRemaining() {
        return (stream.Length - stream.Position) > 0;
    }

    public long Position {
        get => stream.Position;
        set => stream.Position = value;
    }

    public void WriteBytes(byte[] value) {
        stream.Write(value, 0, value.Length);
    }

    public void WriteByte(byte value) {
        stream.WriteByte(value);
    }

    public void WriteUShort(ushort value) {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteUInt(uint value) {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteULong(ulong value) {
        WriteUInt((uint)(value >> 32));
        WriteUInt((uint)value);
    }

    public void WriteSByte(sbyte value) {
        stream.WriteByte((byte)value);
    }

    public void WriteShort(short value) {
        WriteUShort((ushort)value);
    }

    public void WriteInt(int value) {
        WriteUInt((uint)value);
    }

    public void WriteFloat(float value) {
        WriteInt(BitConverter.SingleToInt32Bits(value));
    }

    public void WriteLong(long value) {
        WriteULong((ulong)value);
    }

    public void WriteDouble(double value) {
        WriteLong(BitConverter.DoubleToInt64Bits(value));
    }

    public byte[] ReadBytes(int length) {
        CheckRemaining(length);
        byte[] result = new byte[length];
        stream.Read(result, 0, length);
        return result;
    }

    public byte ReadByte() {
        CheckRemaining(1);
        return Convert.ToByte(stream.ReadByte());
    }

    public ushort ReadUShort() {
        CheckRemaining(2);
        return (ushort)(stream.ReadByte() << 8 | stream.ReadByte());
    }

    public uint ReadUInt() {
        CheckRemaining(4);
        return (uint)(stream.ReadByte() << 24
            | stream.ReadByte() << 16
            | stream.ReadByte() << 8
            | stream.ReadByte());
    }

    public ulong ReadULong() {
        return (ulong)ReadUInt() << 32 | (ulong)ReadUInt();
    }

    public sbyte ReadSByte() {
        return (sbyte)ReadByte();
    }

    public short ReadShort() {
        return (short)ReadUShort();
    }

    public int ReadInt() {
        return (int)ReadUInt();
    }

    public float ReadFloat() {
        return BitConverter.Int32BitsToSingle(ReadInt());
    }

    public long ReadLong() {
        return (long)ReadULong();
    }

    public double ReadDouble() {
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    private void CheckRemaining(int length) {
        if (stream.Length - stream.Position < length) {
            throw new StreamUnderflowException();
        }
    }
}

// Contains loading, initialization code,
// and the FFI Function declarations in a com.sun.jna.Library.


// This is an implementation detail which will be called internally by the public API.
static class _UniFFILib {
    static _UniFFILib() {
        _UniFFILib.uniffiCheckContractApiVersion();
        _UniFFILib.uniffiCheckApiChecksums();
        
        }

    [DllImport("rust_lib")]
    public static extern RustBuffer uniffi_rust_lib_fn_func_simply_fetch_inbox_top(RustBuffer @domain,ushort @port,RustBuffer @username,RustBuffer @password,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer uniffi_rust_lib_fn_func_simply_send_html_email(RustBuffer @smtpServer,RustBuffer @smtpUsername,RustBuffer @smtpPassword,RustBuffer @headers,RustBuffer @plainTextBody,RustBuffer @htmlBody,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer uniffi_rust_lib_fn_func_simply_send_plain_text_email(RustBuffer @smtpServer,RustBuffer @smtpUsername,RustBuffer @smtpPassword,RustBuffer @headers,RustBuffer @body,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rustbuffer_alloc(int @size,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rustbuffer_from_bytes(ForeignBytes @bytes,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rustbuffer_free(RustBuffer @buf,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rustbuffer_reserve(RustBuffer @buf,int @additional,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_continuation_callback_set(IntPtr @callback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u8(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern byte ffi_rust_lib_rust_future_complete_u8(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i8(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i8(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern sbyte ffi_rust_lib_rust_future_complete_i8(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u16(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern ushort ffi_rust_lib_rust_future_complete_u16(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i16(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i16(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern short ffi_rust_lib_rust_future_complete_i16(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u32(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern uint ffi_rust_lib_rust_future_complete_u32(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i32(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern int ffi_rust_lib_rust_future_complete_i32(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_u64(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_u64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_u64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern ulong ffi_rust_lib_rust_future_complete_u64(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_i64(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_i64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_i64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern long ffi_rust_lib_rust_future_complete_i64(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_f32(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_f32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_f32(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern float ffi_rust_lib_rust_future_complete_f32(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_f64(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_f64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_f64(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern double ffi_rust_lib_rust_future_complete_f64(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_pointer(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_pointer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_pointer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern SafeHandle ffi_rust_lib_rust_future_complete_pointer(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_rust_buffer(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_rust_buffer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_rust_buffer(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern RustBuffer ffi_rust_lib_rust_future_complete_rust_buffer(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_poll_void(IntPtr @handle,IntPtr @uniffiCallback
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_cancel_void(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_free_void(IntPtr @handle
    );

    [DllImport("rust_lib")]
    public static extern void ffi_rust_lib_rust_future_complete_void(IntPtr @handle,ref RustCallStatus _uniffi_out_err
    );

    [DllImport("rust_lib")]
    public static extern ushort uniffi_rust_lib_checksum_func_simply_fetch_inbox_top(
    );

    [DllImport("rust_lib")]
    public static extern ushort uniffi_rust_lib_checksum_func_simply_send_html_email(
    );

    [DllImport("rust_lib")]
    public static extern ushort uniffi_rust_lib_checksum_func_simply_send_plain_text_email(
    );

    [DllImport("rust_lib")]
    public static extern uint ffi_rust_lib_uniffi_contract_version(
    );

    

    static void uniffiCheckContractApiVersion() {
        var scaffolding_contract_version = _UniFFILib.ffi_rust_lib_uniffi_contract_version();
        if (24 != scaffolding_contract_version) {
            throw new UniffiContractVersionException($"uniffi.rust_lib: uniffi bindings expected version `24`, library returned `{scaffolding_contract_version}`");
        }
    }

    static void uniffiCheckApiChecksums() {
        {
            var checksum = _UniFFILib.uniffi_rust_lib_checksum_func_simply_fetch_inbox_top();
            if (checksum != 27917) {
                throw new UniffiContractChecksumException($"uniffi.rust_lib: uniffi bindings expected function `uniffi_rust_lib_checksum_func_simply_fetch_inbox_top` checksum `27917`, library returned `{checksum}`");
            }
        }
        {
            var checksum = _UniFFILib.uniffi_rust_lib_checksum_func_simply_send_html_email();
            if (checksum != 64157) {
                throw new UniffiContractChecksumException($"uniffi.rust_lib: uniffi bindings expected function `uniffi_rust_lib_checksum_func_simply_send_html_email` checksum `64157`, library returned `{checksum}`");
            }
        }
        {
            var checksum = _UniFFILib.uniffi_rust_lib_checksum_func_simply_send_plain_text_email();
            if (checksum != 47146) {
                throw new UniffiContractChecksumException($"uniffi.rust_lib: uniffi bindings expected function `uniffi_rust_lib_checksum_func_simply_send_plain_text_email` checksum `47146`, library returned `{checksum}`");
            }
        }
    }
}

// Public interface members begin here.

#pragma warning disable 8625




class FfiConverterUInt8: FfiConverter<byte, byte> {
    public static FfiConverterUInt8 INSTANCE = new FfiConverterUInt8();

    public override byte Lift(byte value) {
        return value;
    }

    public override byte Read(BigEndianStream stream) {
        return stream.ReadByte();
    }

    public override byte Lower(byte value) {
        return value;
    }

    public override int AllocationSize(byte value) {
        return 1;
    }

    public override void Write(byte value, BigEndianStream stream) {
        stream.WriteByte(value);
    }
}



class FfiConverterUInt16: FfiConverter<ushort, ushort> {
    public static FfiConverterUInt16 INSTANCE = new FfiConverterUInt16();

    public override ushort Lift(ushort value) {
        return value;
    }

    public override ushort Read(BigEndianStream stream) {
        return stream.ReadUShort();
    }

    public override ushort Lower(ushort value) {
        return value;
    }

    public override int AllocationSize(ushort value) {
        return 2;
    }

    public override void Write(ushort value, BigEndianStream stream) {
        stream.WriteUShort(value);
    }
}



class FfiConverterString: FfiConverter<string, RustBuffer> {
    public static FfiConverterString INSTANCE = new FfiConverterString();

    // Note: we don't inherit from FfiConverterRustBuffer, because we use a
    // special encoding when lowering/lifting.  We can use `RustBuffer.len` to
    // store our length and avoid writing it out to the buffer.
    public override string Lift(RustBuffer value) {
        try {
            var bytes = value.AsStream().ReadBytes(value.len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        } finally {
            RustBuffer.Free(value);
        }
    }

    public override string Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var bytes = stream.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public override RustBuffer Lower(string value) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var rbuf = RustBuffer.Alloc(bytes.Length);
        rbuf.AsWriteableStream().WriteBytes(bytes);
        return rbuf;
    }

    // TODO(CS)
    // We aren't sure exactly how many bytes our string will be once it's UTF-8
    // encoded.  Allocate 3 bytes per unicode codepoint which will always be
    // enough.
    public override int AllocationSize(string value) {
        const int sizeForLength = 4;
        var sizeForString = value.Length * 3;
        return sizeForLength + sizeForString;
    }

    public override void Write(string value, BigEndianStream stream) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.WriteInt(bytes.Length);
        stream.WriteBytes(bytes);
    }
}



public record SmtpResponse (
    byte @severity, 
    byte @category, 
    byte @detail, 
    String @message
) {
}

class FfiConverterTypeSmtpResponse: FfiConverterRustBuffer<SmtpResponse> {
    public static FfiConverterTypeSmtpResponse INSTANCE = new FfiConverterTypeSmtpResponse();

    public override SmtpResponse Read(BigEndianStream stream) {
        return new SmtpResponse(
            FfiConverterUInt8.INSTANCE.Read(stream),
            FfiConverterUInt8.INSTANCE.Read(stream),
            FfiConverterUInt8.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(SmtpResponse value) {
        return
            FfiConverterUInt8.INSTANCE.AllocationSize(value.@severity) +
            FfiConverterUInt8.INSTANCE.AllocationSize(value.@category) +
            FfiConverterUInt8.INSTANCE.AllocationSize(value.@detail) +
            FfiConverterString.INSTANCE.AllocationSize(value.@message);
    }

    public override void Write(SmtpResponse value, BigEndianStream stream) {
            FfiConverterUInt8.INSTANCE.Write(value.@severity, stream);
            FfiConverterUInt8.INSTANCE.Write(value.@category, stream);
            FfiConverterUInt8.INSTANCE.Write(value.@detail, stream);
            FfiConverterString.INSTANCE.Write(value.@message, stream);
    }
}





public class ImapException: UniffiException {
    ImapException(string message): base(message) {}

    // Each variant is a nested class
    // Flat enums carries a string error message, so no special implementation is necessary.
    
    public class IoException: ImapException {
        public IoException(string message): base(message) {}
    }
    
    public class TlsHandshakeException: ImapException {
        public TlsHandshakeException(string message): base(message) {}
    }
    
    public class TlsException: ImapException {
        public TlsException(string message): base(message) {}
    }
    
    public class BadResponse: ImapException {
        public BadResponse(string message): base(message) {}
    }
    
    public class NoResponse: ImapException {
        public NoResponse(string message): base(message) {}
    }
    
    public class ConnectionLost: ImapException {
        public ConnectionLost(string message): base(message) {}
    }
    
    public class ParseException: ImapException {
        public ParseException(string message): base(message) {}
    }
    
    public class ValidateException: ImapException {
        public ValidateException(string message): base(message) {}
    }
    
    public class AppendException: ImapException {
        public AppendException(string message): base(message) {}
    }
    
    public class Nonexhaustive: ImapException {
        public Nonexhaustive(string message): base(message) {}
    }
    
}

class FfiConverterTypeImapException : FfiConverterRustBuffer<ImapException>, CallStatusErrorHandler<ImapException> {
    public static FfiConverterTypeImapException INSTANCE = new FfiConverterTypeImapException();

    public override ImapException Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1: return new ImapException.IoException(FfiConverterString.INSTANCE.Read(stream));
            case 2: return new ImapException.TlsHandshakeException(FfiConverterString.INSTANCE.Read(stream));
            case 3: return new ImapException.TlsException(FfiConverterString.INSTANCE.Read(stream));
            case 4: return new ImapException.BadResponse(FfiConverterString.INSTANCE.Read(stream));
            case 5: return new ImapException.NoResponse(FfiConverterString.INSTANCE.Read(stream));
            case 6: return new ImapException.ConnectionLost(FfiConverterString.INSTANCE.Read(stream));
            case 7: return new ImapException.ParseException(FfiConverterString.INSTANCE.Read(stream));
            case 8: return new ImapException.ValidateException(FfiConverterString.INSTANCE.Read(stream));
            case 9: return new ImapException.AppendException(FfiConverterString.INSTANCE.Read(stream));
            case 10: return new ImapException.Nonexhaustive(FfiConverterString.INSTANCE.Read(stream));
            default:
                throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeImapException.Read()", value));
        }
    }

    public override int AllocationSize(ImapException value) {
        return 4 + FfiConverterString.INSTANCE.AllocationSize(value.Message);
    }

    public override void Write(ImapException value, BigEndianStream stream) {
        switch (value) {
            case ImapException.IoException:
                stream.WriteInt(1);
                break;
            case ImapException.TlsHandshakeException:
                stream.WriteInt(2);
                break;
            case ImapException.TlsException:
                stream.WriteInt(3);
                break;
            case ImapException.BadResponse:
                stream.WriteInt(4);
                break;
            case ImapException.NoResponse:
                stream.WriteInt(5);
                break;
            case ImapException.ConnectionLost:
                stream.WriteInt(6);
                break;
            case ImapException.ParseException:
                stream.WriteInt(7);
                break;
            case ImapException.ValidateException:
                stream.WriteInt(8);
                break;
            case ImapException.AppendException:
                stream.WriteInt(9);
                break;
            case ImapException.Nonexhaustive:
                stream.WriteInt(10);
                break;
            default:
                throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeImapException.Write()", value));
        }
    }
}





public class SmtpException: UniffiException {
    SmtpException(string message): base(message) {}

    // Each variant is a nested class
    // Flat enums carries a string error message, so no special implementation is necessary.
    
    public class TransientSmtpException: SmtpException {
        public TransientSmtpException(string message): base(message) {}
    }
    
    public class PermanentSmtpException: SmtpException {
        public PermanentSmtpException(string message): base(message) {}
    }
    
    public class ResponseParseException: SmtpException {
        public ResponseParseException(string message): base(message) {}
    }
    
    public class InternalClientException: SmtpException {
        public InternalClientException(string message): base(message) {}
    }
    
    public class ConnectionException: SmtpException {
        public ConnectionException(string message): base(message) {}
    }
    
    public class NetworkException: SmtpException {
        public NetworkException(string message): base(message) {}
    }
    
    public class TlsException: SmtpException {
        public TlsException(string message): base(message) {}
    }
    
    public class Timeout: SmtpException {
        public Timeout(string message): base(message) {}
    }
    
    public class OtherException: SmtpException {
        public OtherException(string message): base(message) {}
    }
    
}

class FfiConverterTypeSmtpException : FfiConverterRustBuffer<SmtpException>, CallStatusErrorHandler<SmtpException> {
    public static FfiConverterTypeSmtpException INSTANCE = new FfiConverterTypeSmtpException();

    public override SmtpException Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1: return new SmtpException.TransientSmtpException(FfiConverterString.INSTANCE.Read(stream));
            case 2: return new SmtpException.PermanentSmtpException(FfiConverterString.INSTANCE.Read(stream));
            case 3: return new SmtpException.ResponseParseException(FfiConverterString.INSTANCE.Read(stream));
            case 4: return new SmtpException.InternalClientException(FfiConverterString.INSTANCE.Read(stream));
            case 5: return new SmtpException.ConnectionException(FfiConverterString.INSTANCE.Read(stream));
            case 6: return new SmtpException.NetworkException(FfiConverterString.INSTANCE.Read(stream));
            case 7: return new SmtpException.TlsException(FfiConverterString.INSTANCE.Read(stream));
            case 8: return new SmtpException.Timeout(FfiConverterString.INSTANCE.Read(stream));
            case 9: return new SmtpException.OtherException(FfiConverterString.INSTANCE.Read(stream));
            default:
                throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeSmtpException.Read()", value));
        }
    }

    public override int AllocationSize(SmtpException value) {
        return 4 + FfiConverterString.INSTANCE.AllocationSize(value.Message);
    }

    public override void Write(SmtpException value, BigEndianStream stream) {
        switch (value) {
            case SmtpException.TransientSmtpException:
                stream.WriteInt(1);
                break;
            case SmtpException.PermanentSmtpException:
                stream.WriteInt(2);
                break;
            case SmtpException.ResponseParseException:
                stream.WriteInt(3);
                break;
            case SmtpException.InternalClientException:
                stream.WriteInt(4);
                break;
            case SmtpException.ConnectionException:
                stream.WriteInt(5);
                break;
            case SmtpException.NetworkException:
                stream.WriteInt(6);
                break;
            case SmtpException.TlsException:
                stream.WriteInt(7);
                break;
            case SmtpException.Timeout:
                stream.WriteInt(8);
                break;
            case SmtpException.OtherException:
                stream.WriteInt(9);
                break;
            default:
                throw new InternalException(String.Format("invalid error value '{0}' in FfiConverterTypeSmtpException.Write()", value));
        }
    }
}




class FfiConverterOptionalString: FfiConverterRustBuffer<String?> {
    public static FfiConverterOptionalString INSTANCE = new FfiConverterOptionalString();

    public override String? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterString.INSTANCE.Read(stream);
    }

    public override int AllocationSize(String? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterString.INSTANCE.AllocationSize((String)value);
        }
    }

    public override void Write(String? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterString.INSTANCE.Write((String)value, stream);
        }
    }
}




class FfiConverterDictionaryStringString: FfiConverterRustBuffer<Dictionary<String, String>> {
    public static FfiConverterDictionaryStringString INSTANCE = new FfiConverterDictionaryStringString();

    public override Dictionary<String, String> Read(BigEndianStream stream) {
        var result = new Dictionary<String, String>();
        var len = stream.ReadInt();
        for (int i = 0; i < len; i++) {
            var key = FfiConverterString.INSTANCE.Read(stream);
            var value = FfiConverterString.INSTANCE.Read(stream);
            result[key] = value;
        }
        return result;
    }

    public override int AllocationSize(Dictionary<String, String> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => {
            return FfiConverterString.INSTANCE.AllocationSize(item.Key) +
                FfiConverterString.INSTANCE.AllocationSize(item.Value);
        }).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(Dictionary<String, String> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        foreach (var item in value) {
            FfiConverterString.INSTANCE.Write(item.Key, stream);
            FfiConverterString.INSTANCE.Write(item.Value, stream);
        }
    }
}
#pragma warning restore 8625
public static class RustLibMethods {
    /// <exception cref="ImapException"></exception>
    public static String? SimplyFetchInboxTop(String @domain, ushort @port, String @username, String @password) {
        return FfiConverterOptionalString.INSTANCE.Lift(
    _UniffiHelpers.RustCallWithError(FfiConverterTypeImapException.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.uniffi_rust_lib_fn_func_simply_fetch_inbox_top(FfiConverterString.INSTANCE.Lower(@domain), FfiConverterUInt16.INSTANCE.Lower(@port), FfiConverterString.INSTANCE.Lower(@username), FfiConverterString.INSTANCE.Lower(@password), ref _status)
));
    }

    /// <exception cref="SmtpException"></exception>
    public static SmtpResponse SimplySendHtmlEmail(String @smtpServer, String @smtpUsername, String @smtpPassword, Dictionary<String, String> @headers, String @plainTextBody, String @htmlBody) {
        return FfiConverterTypeSmtpResponse.INSTANCE.Lift(
    _UniffiHelpers.RustCallWithError(FfiConverterTypeSmtpException.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.uniffi_rust_lib_fn_func_simply_send_html_email(FfiConverterString.INSTANCE.Lower(@smtpServer), FfiConverterString.INSTANCE.Lower(@smtpUsername), FfiConverterString.INSTANCE.Lower(@smtpPassword), FfiConverterDictionaryStringString.INSTANCE.Lower(@headers), FfiConverterString.INSTANCE.Lower(@plainTextBody), FfiConverterString.INSTANCE.Lower(@htmlBody), ref _status)
));
    }

    /// <exception cref="SmtpException"></exception>
    public static SmtpResponse SimplySendPlainTextEmail(String @smtpServer, String @smtpUsername, String @smtpPassword, Dictionary<String, String> @headers, String @body) {
        return FfiConverterTypeSmtpResponse.INSTANCE.Lift(
    _UniffiHelpers.RustCallWithError(FfiConverterTypeSmtpException.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.uniffi_rust_lib_fn_func_simply_send_plain_text_email(FfiConverterString.INSTANCE.Lower(@smtpServer), FfiConverterString.INSTANCE.Lower(@smtpUsername), FfiConverterString.INSTANCE.Lower(@smtpPassword), FfiConverterDictionaryStringString.INSTANCE.Lower(@headers), FfiConverterString.INSTANCE.Lower(@body), ref _status)
));
    }

}

