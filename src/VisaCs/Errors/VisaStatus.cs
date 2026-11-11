namespace VisaCs;

/// <summary>VISA completion and error codes (ViStatus). Values match visa.h.</summary>
public enum VisaStatus
{
    /// <summary>VI_SUCCESS: Operation completed successfully.</summary>
    Success = 0,
    /// <summary>VI_SUCCESS_EVENT_EN: Specified event is already enabled for at least one of the specified mechanisms.</summary>
    SuccessEventEnabled = 0x3FFF0002,
    /// <summary>VI_SUCCESS_EVENT_DIS: Specified event is already disabled for at least one of the specified mechanisms.</summary>
    SuccessEventDisabled = 0x3FFF0003,
    /// <summary>VI_SUCCESS_QUEUE_EMPTY: Operation completed successfully, but queue was already empty.</summary>
    SuccessQueueEmpty = 0x3FFF0004,
    /// <summary>VI_SUCCESS_TERM_CHAR: The specified termination character was read.</summary>
    SuccessTerminationCharacter = 0x3FFF0005,
    /// <summary>VI_SUCCESS_MAX_CNT: The number of bytes read is equal to the input count.</summary>
    SuccessMaxCount = 0x3FFF0006,
    /// <summary>VI_WARN_QUEUE_OVERFLOW: The event queue overflowed; some events were lost.</summary>
    WarningQueueOverflow = 0x3FFF000C,
    /// <summary>VI_WARN_CONFIG_NLOADED: The specified configuration either does not exist or could not be loaded.</summary>
    WarningConfigNotLoaded = 0x3FFF0077,
    /// <summary>VI_SUCCESS_DEV_NPRESENT: Session opened successfully, but the device at the specified address is not responding.</summary>
    SuccessDeviceNotPresent = 0x3FFF007D,
    /// <summary>VI_SUCCESS_TRIG_MAPPED: The path from trigSrc to trigDest is already mapped.</summary>
    SuccessTriggerMapped = 0x3FFF007E,
    /// <summary>VI_SUCCESS_QUEUE_NEMPTY: Wait terminated successfully on receipt of an event notification. There is still at least one more event occurrence available.</summary>
    SuccessQueueNotEmpty = 0x3FFF0080,
    /// <summary>VI_WARN_NULL_OBJECT: The specified object reference is uninitialized.</summary>
    WarningNullObject = 0x3FFF0082,
    /// <summary>VI_WARN_NSUP_ATTR_STATE: Although the specified state of the attribute is valid, it is not supported by this resource implementation.</summary>
    WarningAttributeStateNotSupported = 0x3FFF0084,
    /// <summary>VI_WARN_UNKNOWN_STATUS: The status code passed to the operation could not be interpreted.</summary>
    WarningUnknownStatus = 0x3FFF0085,
    /// <summary>VI_WARN_NSUP_BUF: The specified buffer is not supported.</summary>
    WarningBufferNotSupported = 0x3FFF0088,
    /// <summary>VI_SUCCESS_NCHAIN: Event handled successfully. Do not invoke any other handlers on this session for this event.</summary>
    SuccessNoChain = 0x3FFF0098,
    /// <summary>VI_SUCCESS_NESTED_SHARED: Operation completed successfully, and this session has nested shared locks.</summary>
    SuccessNestedShared = 0x3FFF0099,
    /// <summary>VI_SUCCESS_NESTED_EXCLUSIVE: Operation completed successfully, and this session has nested exclusive locks.</summary>
    SuccessNestedExclusive = 0x3FFF009A,
    /// <summary>VI_SUCCESS_SYNC: Asynchronous operation request was actually performed synchronously.</summary>
    SuccessSync = 0x3FFF009B,
    /// <summary>VI_WARN_EXT_FUNC_NIMPL: The operation succeeded, but a lower level driver did not implement the extended functionality.</summary>
    WarningExtensionFunctionNotImplemented = 0x3FFF00A9,

    /// <summary>VI_ERROR_SYSTEM_ERROR: Unknown system error (miscellaneous error).</summary>
    ErrorSystemError = unchecked((int)0xBFFF0000),
    /// <summary>VI_ERROR_INV_OBJECT: The given session or object reference is invalid.</summary>
    ErrorInvalidObject = unchecked((int)0xBFFF000E),
    /// <summary>VI_ERROR_RSRC_LOCKED: Specified type of lock cannot be obtained, or specified operation cannot be performed, because the resource is locked.</summary>
    ErrorResourceLocked = unchecked((int)0xBFFF000F),
    /// <summary>VI_ERROR_INV_EXPR: Invalid expression specified for search.</summary>
    ErrorInvalidExpression = unchecked((int)0xBFFF0010),
    /// <summary>VI_ERROR_RSRC_NFOUND: Insufficient location information or the device or resource is not present in the system.</summary>
    ErrorResourceNotFound = unchecked((int)0xBFFF0011),
    /// <summary>VI_ERROR_INV_RSRC_NAME: Invalid resource reference specified. Parsing error.</summary>
    ErrorInvalidResourceName = unchecked((int)0xBFFF0012),
    /// <summary>VI_ERROR_INV_ACC_MODE: Invalid access mode.</summary>
    ErrorInvalidAccessMode = unchecked((int)0xBFFF0013),
    /// <summary>VI_ERROR_TMO: Timeout expired before operation completed.</summary>
    ErrorTimeout = unchecked((int)0xBFFF0015),
    /// <summary>VI_ERROR_CLOSING_FAILED: The VISA driver failed to properly close the session or object reference.</summary>
    ErrorClosingFailed = unchecked((int)0xBFFF0016),
    /// <summary>VI_ERROR_INV_DEGREE: Specified degree is invalid.</summary>
    ErrorInvalidDegree = unchecked((int)0xBFFF001B),
    /// <summary>VI_ERROR_INV_JOB_ID: Specified job identifier is invalid.</summary>
    ErrorInvalidJobId = unchecked((int)0xBFFF001C),
    /// <summary>VI_ERROR_NSUP_ATTR: The specified attribute is not defined or supported by the referenced object.</summary>
    ErrorAttributeNotSupported = unchecked((int)0xBFFF001D),
    /// <summary>VI_ERROR_NSUP_ATTR_STATE: The specified state of the attribute is not valid, or is not supported as defined by the object.</summary>
    ErrorAttributeStateNotSupported = unchecked((int)0xBFFF001E),
    /// <summary>VI_ERROR_ATTR_READONLY: The specified attribute is read-only.</summary>
    ErrorAttributeReadOnly = unchecked((int)0xBFFF001F),
    /// <summary>VI_ERROR_INV_LOCK_TYPE: The specified type of lock is not supported by this resource.</summary>
    ErrorInvalidLockType = unchecked((int)0xBFFF0020),
    /// <summary>VI_ERROR_INV_ACCESS_KEY: The access key to the resource associated with the specified session is invalid.</summary>
    ErrorInvalidAccessKey = unchecked((int)0xBFFF0021),
    /// <summary>VI_ERROR_INV_EVENT: Specified event type is not supported by the resource.</summary>
    ErrorInvalidEvent = unchecked((int)0xBFFF0026),
    /// <summary>VI_ERROR_INV_MECH: Invalid mechanism specified.</summary>
    ErrorInvalidMechanism = unchecked((int)0xBFFF0027),
    /// <summary>VI_ERROR_HNDLR_NINSTALLED: A handler was not installed.</summary>
    ErrorHandlerNotInstalled = unchecked((int)0xBFFF0028),
    /// <summary>VI_ERROR_INV_HNDLR_REF: The given handler reference is either invalid or was not installed.</summary>
    ErrorInvalidHandlerReference = unchecked((int)0xBFFF0029),
    /// <summary>VI_ERROR_INV_CONTEXT: Specified event context is invalid.</summary>
    ErrorInvalidContext = unchecked((int)0xBFFF002A),
    /// <summary>VI_ERROR_QUEUE_OVERFLOW: The event queue for the specified type has overflowed.</summary>
    ErrorQueueOverflow = unchecked((int)0xBFFF002D),
    /// <summary>VI_ERROR_NENABLED: You must be enabled for events of the specified type in order to receive them.</summary>
    ErrorNotEnabled = unchecked((int)0xBFFF002F),
    /// <summary>VI_ERROR_ABORT: User abort occurred during transfer.</summary>
    ErrorAbort = unchecked((int)0xBFFF0030),
    /// <summary>VI_ERROR_RAW_WR_PROT_VIOL: Violation of raw write protocol occurred during transfer.</summary>
    ErrorRawWriteProtocolViolation = unchecked((int)0xBFFF0034),
    /// <summary>VI_ERROR_RAW_RD_PROT_VIOL: Violation of raw read protocol occurred during transfer.</summary>
    ErrorRawReadProtocolViolation = unchecked((int)0xBFFF0035),
    /// <summary>VI_ERROR_OUTP_PROT_VIOL: Device reported an output protocol error during transfer.</summary>
    ErrorOutputProtocolViolation = unchecked((int)0xBFFF0036),
    /// <summary>VI_ERROR_INP_PROT_VIOL: Device reported an input protocol error during transfer.</summary>
    ErrorInputProtocolViolation = unchecked((int)0xBFFF0037),
    /// <summary>VI_ERROR_BERR: Bus error occurred during transfer.</summary>
    ErrorBusError = unchecked((int)0xBFFF0038),
    /// <summary>VI_ERROR_IN_PROGRESS: Unable to queue the asynchronous operation because there is already an operation in progress.</summary>
    ErrorInProgress = unchecked((int)0xBFFF0039),
    /// <summary>VI_ERROR_INV_SETUP: Unable to start operation because setup is invalid (due to attributes being set to an inconsistent state).</summary>
    ErrorInvalidSetup = unchecked((int)0xBFFF003A),
    /// <summary>VI_ERROR_QUEUE_ERROR: Unable to queue asynchronous operation.</summary>
    ErrorQueueError = unchecked((int)0xBFFF003B),
    /// <summary>VI_ERROR_ALLOC: Insufficient system resources to perform necessary memory allocation.</summary>
    ErrorAllocation = unchecked((int)0xBFFF003C),
    /// <summary>VI_ERROR_INV_MASK: Invalid buffer mask specified.</summary>
    ErrorInvalidMask = unchecked((int)0xBFFF003D),
    /// <summary>VI_ERROR_IO: Could not perform operation because of I/O error.</summary>
    ErrorIO = unchecked((int)0xBFFF003E),
    /// <summary>VI_ERROR_INV_FMT: A format specifier in the format string is invalid.</summary>
    ErrorInvalidFormat = unchecked((int)0xBFFF003F),
    /// <summary>VI_ERROR_NSUP_FMT: A format specifier in the format string is not supported.</summary>
    ErrorFormatNotSupported = unchecked((int)0xBFFF0041),
    /// <summary>VI_ERROR_LINE_IN_USE: The specified trigger line is currently in use.</summary>
    ErrorLineInUse = unchecked((int)0xBFFF0042),
    /// <summary>VI_ERROR_NSUP_MODE: The specified mode is not supported by this VISA implementation.</summary>
    ErrorModeNotSupported = unchecked((int)0xBFFF0046),
    /// <summary>VI_ERROR_SRQ_NOCCURRED: Service request has not been received for the session.</summary>
    ErrorSrqNotOccurred = unchecked((int)0xBFFF004A),
    /// <summary>VI_ERROR_INV_SPACE: Invalid address space specified.</summary>
    ErrorInvalidSpace = unchecked((int)0xBFFF004E),
    /// <summary>VI_ERROR_INV_OFFSET: Invalid offset specified.</summary>
    ErrorInvalidOffset = unchecked((int)0xBFFF0051),
    /// <summary>VI_ERROR_INV_WIDTH: Invalid access width specified.</summary>
    ErrorInvalidWidth = unchecked((int)0xBFFF0052),
    /// <summary>VI_ERROR_NSUP_OFFSET: Specified offset is not accessible from this hardware.</summary>
    ErrorOffsetNotSupported = unchecked((int)0xBFFF0054),
    /// <summary>VI_ERROR_NSUP_VAR_WIDTH: Cannot support source and destination widths that are different.</summary>
    ErrorVariableWidthNotSupported = unchecked((int)0xBFFF0055),
    /// <summary>VI_ERROR_WINDOW_NMAPPED: The specified session is not currently mapped.</summary>
    ErrorWindowNotMapped = unchecked((int)0xBFFF0057),
    /// <summary>VI_ERROR_RESP_PENDING: A previous response is still pending, causing a multiple query error.</summary>
    ErrorResponsePending = unchecked((int)0xBFFF0059),
    /// <summary>VI_ERROR_NLISTENERS: No listeners condition is detected (both NRFD and NDAC are deasserted).</summary>
    ErrorNoListeners = unchecked((int)0xBFFF005F),
    /// <summary>VI_ERROR_NCIC: The interface associated with this session is not currently the controller in charge.</summary>
    ErrorNotControllerInCharge = unchecked((int)0xBFFF0060),
    /// <summary>VI_ERROR_NSYS_CNTLR: The interface associated with this session is not the system controller.</summary>
    ErrorNotSystemController = unchecked((int)0xBFFF0061),
    /// <summary>VI_ERROR_NSUP_OPER: The given session or object reference does not support this operation.</summary>
    ErrorOperationNotSupported = unchecked((int)0xBFFF0067),
    /// <summary>VI_ERROR_INTR_PENDING: An interrupt is still pending from a previous call.</summary>
    ErrorInterruptPending = unchecked((int)0xBFFF0068),
    /// <summary>VI_ERROR_ASRL_PARITY: A parity error occurred during transfer.</summary>
    ErrorSerialParity = unchecked((int)0xBFFF006A),
    /// <summary>VI_ERROR_ASRL_FRAMING: A framing error occurred during transfer.</summary>
    ErrorSerialFraming = unchecked((int)0xBFFF006B),
    /// <summary>VI_ERROR_ASRL_OVERRUN: An overrun error occurred during transfer. A character was not read from the hardware before the next character arrived.</summary>
    ErrorSerialOverrun = unchecked((int)0xBFFF006C),
    /// <summary>VI_ERROR_TRIG_NMAPPED: The path from trigSrc to trigDest is not currently mapped.</summary>
    ErrorTriggerNotMapped = unchecked((int)0xBFFF006E),
    /// <summary>VI_ERROR_NSUP_ALIGN_OFFSET: The specified offset is not properly aligned for the access width of the operation.</summary>
    ErrorAlignmentOffsetNotSupported = unchecked((int)0xBFFF0070),
    /// <summary>VI_ERROR_USER_BUF: A specified user buffer is not valid or cannot be accessed for the required size.</summary>
    ErrorUserBuffer = unchecked((int)0xBFFF0071),
    /// <summary>VI_ERROR_RSRC_BUSY: The resource is valid, but VISA cannot currently access it.</summary>
    ErrorResourceBusy = unchecked((int)0xBFFF0072),
    /// <summary>VI_ERROR_NSUP_WIDTH: Specified width is not supported by this hardware.</summary>
    ErrorWidthNotSupported = unchecked((int)0xBFFF0076),
    /// <summary>VI_ERROR_INV_PARAMETER: The value of some parameter is invalid.</summary>
    ErrorInvalidParameter = unchecked((int)0xBFFF0078),
    /// <summary>VI_ERROR_INV_PROT: The protocol specified is invalid.</summary>
    ErrorInvalidProtocol = unchecked((int)0xBFFF0079),
    /// <summary>VI_ERROR_INV_SIZE: Invalid size of window specified.</summary>
    ErrorInvalidSize = unchecked((int)0xBFFF007B),
    /// <summary>VI_ERROR_WINDOW_MAPPED: The specified session currently contains a mapped window.</summary>
    ErrorWindowMapped = unchecked((int)0xBFFF0080),
    /// <summary>VI_ERROR_NIMPL_OPER: The given operation is not implemented.</summary>
    ErrorOperationNotImplemented = unchecked((int)0xBFFF0081),
    /// <summary>VI_ERROR_INV_LENGTH: Invalid length specified.</summary>
    ErrorInvalidLength = unchecked((int)0xBFFF0083),
    /// <summary>VI_ERROR_INV_MODE: The specified mode is invalid.</summary>
    ErrorInvalidMode = unchecked((int)0xBFFF0091),
    /// <summary>VI_ERROR_SESN_NLOCKED: The current session did not have any lock on the resource.</summary>
    ErrorSessionNotLocked = unchecked((int)0xBFFF009C),
    /// <summary>VI_ERROR_MEM_NSHARED: The device does not export any memory.</summary>
    ErrorMemoryNotShared = unchecked((int)0xBFFF009D),
    /// <summary>VI_ERROR_LIBRARY_NFOUND: A code library required by VISA could not be located or loaded.</summary>
    ErrorLibraryNotFound = unchecked((int)0xBFFF009E),
    /// <summary>VI_ERROR_NSUP_INTR: The interface cannot generate an interrupt on the requested level or with the requested statusID value.</summary>
    ErrorInterruptNotSupported = unchecked((int)0xBFFF009F),
    /// <summary>VI_ERROR_INV_LINE: The value specified by the line parameter is invalid.</summary>
    ErrorInvalidLine = unchecked((int)0xBFFF00A0),
    /// <summary>VI_ERROR_FILE_ACCESS: An error occurred while trying to open the specified file.</summary>
    ErrorFileAccess = unchecked((int)0xBFFF00A1),
    /// <summary>VI_ERROR_FILE_IO: An error occurred while performing I/O on the specified file.</summary>
    ErrorFileIO = unchecked((int)0xBFFF00A2),
    /// <summary>VI_ERROR_NSUP_LINE: One of the specified lines is not supported by this VISA implementation.</summary>
    ErrorLineNotSupported = unchecked((int)0xBFFF00A3),
    /// <summary>VI_ERROR_NSUP_MECH: The specified mechanism is not supported for the given event type.</summary>
    ErrorMechanismNotSupported = unchecked((int)0xBFFF00A4),
    /// <summary>VI_ERROR_INTF_NUM_NCONFIG: The interface type is valid but the specified interface number is not configured.</summary>
    ErrorInterfaceNumberNotConfigured = unchecked((int)0xBFFF00A5),
    /// <summary>VI_ERROR_CONN_LOST: The connection for the given session has been lost.</summary>
    ErrorConnectionLost = unchecked((int)0xBFFF00A6),
    /// <summary>VI_ERROR_MACHINE_NAVAIL: The remote machine does not exist or is not accepting any connections.</summary>
    ErrorMachineNotAvailable = unchecked((int)0xBFFF00A7),
    /// <summary>VI_ERROR_NPERMISSION: Access to the resource or remote machine is denied.</summary>
    ErrorNoPermission = unchecked((int)0xBFFF00A8),
}

/// <summary>Names and descriptions of VISA status codes.</summary>
public static class VisaStatusInfo
{
    private static readonly Dictionary<VisaStatus, (string Name, string Description)> Table = new()
    {
        [VisaStatus.Success] = ("VI_SUCCESS", "Operation completed successfully."),
        [VisaStatus.SuccessEventEnabled] = ("VI_SUCCESS_EVENT_EN", "Specified event is already enabled for at least one of the specified mechanisms."),
        [VisaStatus.SuccessEventDisabled] = ("VI_SUCCESS_EVENT_DIS", "Specified event is already disabled for at least one of the specified mechanisms."),
        [VisaStatus.SuccessQueueEmpty] = ("VI_SUCCESS_QUEUE_EMPTY", "Operation completed successfully, but queue was already empty."),
        [VisaStatus.SuccessTerminationCharacter] = ("VI_SUCCESS_TERM_CHAR", "The specified termination character was read."),
        [VisaStatus.SuccessMaxCount] = ("VI_SUCCESS_MAX_CNT", "The number of bytes read is equal to the input count."),
        [VisaStatus.WarningQueueOverflow] = ("VI_WARN_QUEUE_OVERFLOW", "The event queue overflowed; some events were lost."),
        [VisaStatus.WarningConfigNotLoaded] = ("VI_WARN_CONFIG_NLOADED", "The specified configuration either does not exist or could not be loaded."),
        [VisaStatus.SuccessDeviceNotPresent] = ("VI_SUCCESS_DEV_NPRESENT", "Session opened successfully, but the device at the specified address is not responding."),
        [VisaStatus.SuccessTriggerMapped] = ("VI_SUCCESS_TRIG_MAPPED", "The path from trigSrc to trigDest is already mapped."),
        [VisaStatus.SuccessQueueNotEmpty] = ("VI_SUCCESS_QUEUE_NEMPTY", "Wait terminated successfully on receipt of an event notification. There is still at least one more event occurrence available."),
        [VisaStatus.WarningNullObject] = ("VI_WARN_NULL_OBJECT", "The specified object reference is uninitialized."),
        [VisaStatus.WarningAttributeStateNotSupported] = ("VI_WARN_NSUP_ATTR_STATE", "Although the specified state of the attribute is valid, it is not supported by this resource implementation."),
        [VisaStatus.WarningUnknownStatus] = ("VI_WARN_UNKNOWN_STATUS", "The status code passed to the operation could not be interpreted."),
        [VisaStatus.WarningBufferNotSupported] = ("VI_WARN_NSUP_BUF", "The specified buffer is not supported."),
        [VisaStatus.SuccessNoChain] = ("VI_SUCCESS_NCHAIN", "Event handled successfully. Do not invoke any other handlers on this session for this event."),
        [VisaStatus.SuccessNestedShared] = ("VI_SUCCESS_NESTED_SHARED", "Operation completed successfully, and this session has nested shared locks."),
        [VisaStatus.SuccessNestedExclusive] = ("VI_SUCCESS_NESTED_EXCLUSIVE", "Operation completed successfully, and this session has nested exclusive locks."),
        [VisaStatus.SuccessSync] = ("VI_SUCCESS_SYNC", "Asynchronous operation request was actually performed synchronously."),
        [VisaStatus.WarningExtensionFunctionNotImplemented] = ("VI_WARN_EXT_FUNC_NIMPL", "The operation succeeded, but a lower level driver did not implement the extended functionality."),
        [VisaStatus.ErrorSystemError] = ("VI_ERROR_SYSTEM_ERROR", "Unknown system error (miscellaneous error)."),
        [VisaStatus.ErrorInvalidObject] = ("VI_ERROR_INV_OBJECT", "The given session or object reference is invalid."),
        [VisaStatus.ErrorResourceLocked] = ("VI_ERROR_RSRC_LOCKED", "Specified type of lock cannot be obtained, or specified operation cannot be performed, because the resource is locked."),
        [VisaStatus.ErrorInvalidExpression] = ("VI_ERROR_INV_EXPR", "Invalid expression specified for search."),
        [VisaStatus.ErrorResourceNotFound] = ("VI_ERROR_RSRC_NFOUND", "Insufficient location information or the device or resource is not present in the system."),
        [VisaStatus.ErrorInvalidResourceName] = ("VI_ERROR_INV_RSRC_NAME", "Invalid resource reference specified. Parsing error."),
        [VisaStatus.ErrorInvalidAccessMode] = ("VI_ERROR_INV_ACC_MODE", "Invalid access mode."),
        [VisaStatus.ErrorTimeout] = ("VI_ERROR_TMO", "Timeout expired before operation completed."),
        [VisaStatus.ErrorClosingFailed] = ("VI_ERROR_CLOSING_FAILED", "The VISA driver failed to properly close the session or object reference."),
        [VisaStatus.ErrorInvalidDegree] = ("VI_ERROR_INV_DEGREE", "Specified degree is invalid."),
        [VisaStatus.ErrorInvalidJobId] = ("VI_ERROR_INV_JOB_ID", "Specified job identifier is invalid."),
        [VisaStatus.ErrorAttributeNotSupported] = ("VI_ERROR_NSUP_ATTR", "The specified attribute is not defined or supported by the referenced object."),
        [VisaStatus.ErrorAttributeStateNotSupported] = ("VI_ERROR_NSUP_ATTR_STATE", "The specified state of the attribute is not valid, or is not supported as defined by the object."),
        [VisaStatus.ErrorAttributeReadOnly] = ("VI_ERROR_ATTR_READONLY", "The specified attribute is read-only."),
        [VisaStatus.ErrorInvalidLockType] = ("VI_ERROR_INV_LOCK_TYPE", "The specified type of lock is not supported by this resource."),
        [VisaStatus.ErrorInvalidAccessKey] = ("VI_ERROR_INV_ACCESS_KEY", "The access key to the resource associated with the specified session is invalid."),
        [VisaStatus.ErrorInvalidEvent] = ("VI_ERROR_INV_EVENT", "Specified event type is not supported by the resource."),
        [VisaStatus.ErrorInvalidMechanism] = ("VI_ERROR_INV_MECH", "Invalid mechanism specified."),
        [VisaStatus.ErrorHandlerNotInstalled] = ("VI_ERROR_HNDLR_NINSTALLED", "A handler was not installed."),
        [VisaStatus.ErrorInvalidHandlerReference] = ("VI_ERROR_INV_HNDLR_REF", "The given handler reference is either invalid or was not installed."),
        [VisaStatus.ErrorInvalidContext] = ("VI_ERROR_INV_CONTEXT", "Specified event context is invalid."),
        [VisaStatus.ErrorQueueOverflow] = ("VI_ERROR_QUEUE_OVERFLOW", "The event queue for the specified type has overflowed."),
        [VisaStatus.ErrorNotEnabled] = ("VI_ERROR_NENABLED", "You must be enabled for events of the specified type in order to receive them."),
        [VisaStatus.ErrorAbort] = ("VI_ERROR_ABORT", "User abort occurred during transfer."),
        [VisaStatus.ErrorRawWriteProtocolViolation] = ("VI_ERROR_RAW_WR_PROT_VIOL", "Violation of raw write protocol occurred during transfer."),
        [VisaStatus.ErrorRawReadProtocolViolation] = ("VI_ERROR_RAW_RD_PROT_VIOL", "Violation of raw read protocol occurred during transfer."),
        [VisaStatus.ErrorOutputProtocolViolation] = ("VI_ERROR_OUTP_PROT_VIOL", "Device reported an output protocol error during transfer."),
        [VisaStatus.ErrorInputProtocolViolation] = ("VI_ERROR_INP_PROT_VIOL", "Device reported an input protocol error during transfer."),
        [VisaStatus.ErrorBusError] = ("VI_ERROR_BERR", "Bus error occurred during transfer."),
        [VisaStatus.ErrorInProgress] = ("VI_ERROR_IN_PROGRESS", "Unable to queue the asynchronous operation because there is already an operation in progress."),
        [VisaStatus.ErrorInvalidSetup] = ("VI_ERROR_INV_SETUP", "Unable to start operation because setup is invalid (due to attributes being set to an inconsistent state)."),
        [VisaStatus.ErrorQueueError] = ("VI_ERROR_QUEUE_ERROR", "Unable to queue asynchronous operation."),
        [VisaStatus.ErrorAllocation] = ("VI_ERROR_ALLOC", "Insufficient system resources to perform necessary memory allocation."),
        [VisaStatus.ErrorInvalidMask] = ("VI_ERROR_INV_MASK", "Invalid buffer mask specified."),
        [VisaStatus.ErrorIO] = ("VI_ERROR_IO", "Could not perform operation because of I/O error."),
        [VisaStatus.ErrorInvalidFormat] = ("VI_ERROR_INV_FMT", "A format specifier in the format string is invalid."),
        [VisaStatus.ErrorFormatNotSupported] = ("VI_ERROR_NSUP_FMT", "A format specifier in the format string is not supported."),
        [VisaStatus.ErrorLineInUse] = ("VI_ERROR_LINE_IN_USE", "The specified trigger line is currently in use."),
        [VisaStatus.ErrorModeNotSupported] = ("VI_ERROR_NSUP_MODE", "The specified mode is not supported by this VISA implementation."),
        [VisaStatus.ErrorSrqNotOccurred] = ("VI_ERROR_SRQ_NOCCURRED", "Service request has not been received for the session."),
        [VisaStatus.ErrorInvalidSpace] = ("VI_ERROR_INV_SPACE", "Invalid address space specified."),
        [VisaStatus.ErrorInvalidOffset] = ("VI_ERROR_INV_OFFSET", "Invalid offset specified."),
        [VisaStatus.ErrorInvalidWidth] = ("VI_ERROR_INV_WIDTH", "Invalid access width specified."),
        [VisaStatus.ErrorOffsetNotSupported] = ("VI_ERROR_NSUP_OFFSET", "Specified offset is not accessible from this hardware."),
        [VisaStatus.ErrorVariableWidthNotSupported] = ("VI_ERROR_NSUP_VAR_WIDTH", "Cannot support source and destination widths that are different."),
        [VisaStatus.ErrorWindowNotMapped] = ("VI_ERROR_WINDOW_NMAPPED", "The specified session is not currently mapped."),
        [VisaStatus.ErrorResponsePending] = ("VI_ERROR_RESP_PENDING", "A previous response is still pending, causing a multiple query error."),
        [VisaStatus.ErrorNoListeners] = ("VI_ERROR_NLISTENERS", "No listeners condition is detected (both NRFD and NDAC are deasserted)."),
        [VisaStatus.ErrorNotControllerInCharge] = ("VI_ERROR_NCIC", "The interface associated with this session is not currently the controller in charge."),
        [VisaStatus.ErrorNotSystemController] = ("VI_ERROR_NSYS_CNTLR", "The interface associated with this session is not the system controller."),
        [VisaStatus.ErrorOperationNotSupported] = ("VI_ERROR_NSUP_OPER", "The given session or object reference does not support this operation."),
        [VisaStatus.ErrorInterruptPending] = ("VI_ERROR_INTR_PENDING", "An interrupt is still pending from a previous call."),
        [VisaStatus.ErrorSerialParity] = ("VI_ERROR_ASRL_PARITY", "A parity error occurred during transfer."),
        [VisaStatus.ErrorSerialFraming] = ("VI_ERROR_ASRL_FRAMING", "A framing error occurred during transfer."),
        [VisaStatus.ErrorSerialOverrun] = ("VI_ERROR_ASRL_OVERRUN", "An overrun error occurred during transfer. A character was not read from the hardware before the next character arrived."),
        [VisaStatus.ErrorTriggerNotMapped] = ("VI_ERROR_TRIG_NMAPPED", "The path from trigSrc to trigDest is not currently mapped."),
        [VisaStatus.ErrorAlignmentOffsetNotSupported] = ("VI_ERROR_NSUP_ALIGN_OFFSET", "The specified offset is not properly aligned for the access width of the operation."),
        [VisaStatus.ErrorUserBuffer] = ("VI_ERROR_USER_BUF", "A specified user buffer is not valid or cannot be accessed for the required size."),
        [VisaStatus.ErrorResourceBusy] = ("VI_ERROR_RSRC_BUSY", "The resource is valid, but VISA cannot currently access it."),
        [VisaStatus.ErrorWidthNotSupported] = ("VI_ERROR_NSUP_WIDTH", "Specified width is not supported by this hardware."),
        [VisaStatus.ErrorInvalidParameter] = ("VI_ERROR_INV_PARAMETER", "The value of some parameter is invalid."),
        [VisaStatus.ErrorInvalidProtocol] = ("VI_ERROR_INV_PROT", "The protocol specified is invalid."),
        [VisaStatus.ErrorInvalidSize] = ("VI_ERROR_INV_SIZE", "Invalid size of window specified."),
        [VisaStatus.ErrorWindowMapped] = ("VI_ERROR_WINDOW_MAPPED", "The specified session currently contains a mapped window."),
        [VisaStatus.ErrorOperationNotImplemented] = ("VI_ERROR_NIMPL_OPER", "The given operation is not implemented."),
        [VisaStatus.ErrorInvalidLength] = ("VI_ERROR_INV_LENGTH", "Invalid length specified."),
        [VisaStatus.ErrorInvalidMode] = ("VI_ERROR_INV_MODE", "The specified mode is invalid."),
        [VisaStatus.ErrorSessionNotLocked] = ("VI_ERROR_SESN_NLOCKED", "The current session did not have any lock on the resource."),
        [VisaStatus.ErrorMemoryNotShared] = ("VI_ERROR_MEM_NSHARED", "The device does not export any memory."),
        [VisaStatus.ErrorLibraryNotFound] = ("VI_ERROR_LIBRARY_NFOUND", "A code library required by VISA could not be located or loaded."),
        [VisaStatus.ErrorInterruptNotSupported] = ("VI_ERROR_NSUP_INTR", "The interface cannot generate an interrupt on the requested level or with the requested statusID value."),
        [VisaStatus.ErrorInvalidLine] = ("VI_ERROR_INV_LINE", "The value specified by the line parameter is invalid."),
        [VisaStatus.ErrorFileAccess] = ("VI_ERROR_FILE_ACCESS", "An error occurred while trying to open the specified file."),
        [VisaStatus.ErrorFileIO] = ("VI_ERROR_FILE_IO", "An error occurred while performing I/O on the specified file."),
        [VisaStatus.ErrorLineNotSupported] = ("VI_ERROR_NSUP_LINE", "One of the specified lines is not supported by this VISA implementation."),
        [VisaStatus.ErrorMechanismNotSupported] = ("VI_ERROR_NSUP_MECH", "The specified mechanism is not supported for the given event type."),
        [VisaStatus.ErrorInterfaceNumberNotConfigured] = ("VI_ERROR_INTF_NUM_NCONFIG", "The interface type is valid but the specified interface number is not configured."),
        [VisaStatus.ErrorConnectionLost] = ("VI_ERROR_CONN_LOST", "The connection for the given session has been lost."),
        [VisaStatus.ErrorMachineNotAvailable] = ("VI_ERROR_MACHINE_NAVAIL", "The remote machine does not exist or is not accepting any connections."),
        [VisaStatus.ErrorNoPermission] = ("VI_ERROR_NPERMISSION", "Access to the resource or remote machine is denied."),
    };

    /// <summary>Gets the visa.h name of a status code, e.g. <c>VI_ERROR_TMO</c>.</summary>
    public static string GetName(VisaStatus status) =>
        Table.TryGetValue(status, out var e) ? e.Name : $"0x{(uint)status:X8}";

    /// <summary>Gets a human readable description of a status code.</summary>
    public static string GetDescription(VisaStatus status) =>
        Table.TryGetValue(status, out var e) ? e.Description : "Unknown status code.";

    /// <summary>True if the status represents an error (negative ViStatus).</summary>
    public static bool IsError(this VisaStatus status) => (int)status < 0;
}
