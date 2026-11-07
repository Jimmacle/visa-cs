"""
Drives VisaCs simulator servers with PyVISA + PyVISA-py, an independent VISA implementation.

Usage: python pyvisa_client_interop.py RESOURCE [RESOURCE ...]
Start the servers first with:  visashell simulate --bind 127.0.0.1 --portmapper 111 --hislip 4880 --socket 5025 --vicp 1861
Exit code 0 when every check passes.
"""
import sys
import pyvisa
from pyvisa import constants

failures = 0


def check(resource, name, condition, detail=""):
    global failures
    status = "PASS" if condition else "FAIL"
    if not condition:
        failures += 1
    print(f"{status}  {resource:45} {name}{(' - ' + str(detail)) if detail and not condition else ''}")


skipped = 0


def attempt(resource, name, action):
    global skipped
    try:
        action()
        check(resource, name, True)
    except NotImplementedError:
        skipped += 1
        print(f"SKIP  {resource:45} {name} - not implemented by PyVISA-py")
    except pyvisa.errors.VisaIOError as ex:
        if ex.error_code == constants.StatusCode.error_nonsupported_operation:
            skipped += 1
            print(f"SKIP  {resource:45} {name} - not supported by PyVISA-py for this interface")
        else:
            check(resource, name, False, f"{type(ex).__name__}: {ex}")
    except Exception as ex:  # noqa: BLE001 - report every failure
        check(resource, name, False, f"{type(ex).__name__}: {ex}")


def exercise(rm, resource):
    inst = rm.open_resource(resource)
    inst.timeout = 5000
    inst.read_termination = "\n"
    inst.write_termination = "\n"
    is_socket = resource.upper().endswith("SOCKET")
    is_vicp = resource.upper().startswith("VICP")

    idn = inst.query("*IDN?")
    check(resource, "query *IDN?", idn.startswith("VisaCs,Simulated Instrument"), idn)
    check(resource, "query echo", inst.query("ECHO? hello world") == "hello world")
    values = inst.query_ascii_values("DATA:ASCII? 10")
    check(resource, "ascii values", len(values) == 10 and abs(values[9] - 4.5) < 1e-9, values)
    floats = inst.query_binary_values("DATA:FLOAT? 100", datatype="f", is_big_endian=False)
    check(resource, "binary float block", len(floats) == 100 and floats[99] == 49.5, len(floats))
    big = inst.query_binary_values("DATA:BIN? 100000", datatype="B", container=bytes)
    check(resource, "100 kB binary block", len(big) == 100000 and big[1234] == 1234 % 256, len(big))
    inst.write_binary_values("DATA:STORE ", list(range(256)), datatype="B")
    back = inst.query_binary_values("DATA:STORE?", datatype="B", container=list)
    check(resource, "write binary block", back == list(range(256)), back[:8])

    if not is_socket:
        def stb():
            # Set status bit 0 without enabling service requests (PyVISA-py has no SRQ event handling).
            inst.write("*CLS;*SRE 0;SRQ")
            inst.query("*OPC?")
            value = inst.read_stb()
            if value & 0x01 != 0x01:
                raise AssertionError(f"STB=0x{value:02X}")
            inst.write("*CLS")
            inst.query("*OPC?")
        attempt(resource, "read_stb", stb)
        attempt(resource, "assert_trigger", inst.assert_trigger)
        attempt(resource, "clear", inst.clear)
        check(resource, "query after clear", inst.query("*IDN?") == idn)

    if not is_socket and not is_vicp:
        def lock():
            inst.lock_excl(timeout=1000)
            inst.query("*IDN?")
            inst.unlock()
        attempt(resource, "exclusive lock", lock)
        attempt(resource, "remote/local", lambda: (inst.control_ren(3),   # VI_GPIB_REN_ASSERT_ADDRESS
                                                   inst.control_ren(2)))  # VI_GPIB_REN_DEASSERT_GTL

    if not is_socket:
        def timeout_and_recover():
            inst.timeout = 300
            try:
                inst.query("DELAY? 1200")
                raise AssertionError("no timeout")
            except pyvisa.errors.VisaIOError as ex:
                if ex.error_code != constants.StatusCode.error_timeout:
                    raise
            inst.timeout = 5000
            inst.clear()
            if inst.query("*IDN?") != idn:
                raise AssertionError("not resynchronized")
        attempt(resource, "timeout and recovery", timeout_and_recover)

    inst.close()


def main():
    rm = pyvisa.ResourceManager("@py")
    print(f"PyVISA {pyvisa.__version__} with {rm.visalib}")
    for resource in sys.argv[1:]:
        try:
            exercise(rm, resource)
        except Exception as ex:  # noqa: BLE001
            check(resource, "session", False, f"{type(ex).__name__}: {ex}")
    print(f"{failures} failure(s), {skipped} skipped (unsupported by PyVISA-py)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
