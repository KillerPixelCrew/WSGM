# Trace trace.etl

Duration 53.9 s. Samples and switches are whole-trace totals; rates are per second of trace.

## Processes

| Process                           |   PID | CPU ms | CPU % of one core | Context switches | Switches/s |
| --------------------------------- | ----: | -----: | ----------------: | ---------------: | ---------: |
| WmiPrvSE.exe                      |  8620 |  18170 |             33.69 |             4961 |         92 |
| WSGM.exe                          |  4532 |   6646 |             12.32 |           132377 |       2454 |
| svchost.exe                       |  1744 |   4781 |              8.87 |             8763 |        162 |
| Idle                              |     0 |   3119 |              5.78 |           230696 |       4277 |
| WUDFHost.exe                      |  1404 |   3040 |              5.64 |            63932 |       1185 |
| pwsh.exe                          | 14288 |   2933 |              5.44 |             4429 |         82 |
| claude.exe                        | 12608 |   2848 |              5.28 |            18083 |        335 |
| System                            |     4 |   2182 |              4.05 |            46504 |        862 |
| rustdesk.exe                      |  5592 |   1166 |              2.16 |            13954 |        259 |
| IntelGraphicsSoftware.exe         | 11516 |   1156 |              2.14 |             4000 |         74 |
| WmiPrvSE.exe                      | 12840 |    890 |              1.65 |             3787 |         70 |
| esrv_svc.exe                      |  6472 |    774 |              1.43 |             2333 |         43 |
| VBCSCompiler.exe                  | 13432 |    736 |              1.37 |              485 |          9 |
| rustdesk.exe                      | 14400 |    727 |              1.35 |             8578 |        159 |
| svchost.exe                       |  4320 |    611 |              1.13 |             2294 |         43 |
| dotnet-counters.exe               | 10080 |    596 |              1.10 |             4261 |         79 |
| DSAService.exe                    |  4008 |    570 |              1.06 |              906 |         17 |
| steam.exe                         |  8660 |    522 |              0.97 |             7891 |        146 |
| dwm.exe                           |  1600 |    502 |              0.93 |             2656 |         49 |
| steamwebhelper.exe                |  1648 |    496 |              0.92 |              817 |         15 |
| pwsh.exe                          |   752 |    473 |              0.88 |             1342 |         25 |
| WindowsTerminal.exe               | 11040 |    428 |              0.79 |             2152 |         40 |
| svchost.exe                       |  3356 |    408 |              0.76 |             3845 |         71 |
| steamwebhelper.exe                |  3288 |    381 |              0.71 |             4078 |         76 |
| taskhostw.exe                     |  5864 |    356 |              0.66 |              882 |         16 |
| DSATray.exe                       | 11132 |    315 |              0.58 |              131 |          2 |
| wpr.exe                           | 13852 |    303 |              0.56 |              104 |          2 |
| IntelGraphicsSoftware.Overlay.exe | 12236 |    297 |              0.55 |              132 |          2 |
| rustdesk.exe                      |  6956 |    294 |              0.54 |             5956 |        110 |
| csrss.exe                         |  1044 |    271 |              0.50 |             3731 |         69 |
| iGoSwServer.exe                   |  5972 |    263 |              0.49 |             1623 |         30 |
| svchost.exe                       |  1324 |    258 |              0.48 |             2186 |         41 |
| pwsh.exe                          |  8360 |    200 |              0.37 |              443 |          8 |
| svchost.exe                       |  1492 |    183 |              0.34 |             1170 |         22 |
| PresentMonService.exe             |  6424 |    182 |              0.34 |             1445 |         27 |
| tail.exe                          | 10284 |    175 |              0.32 |             3045 |         56 |
| IntelGraphicsSoftware.Service.exe |  2792 |    168 |              0.31 |               74 |          1 |
| svchost.exe                       |  1732 |    146 |              0.27 |              830 |         15 |
| LHMDataProvider.exe               |  9776 |    146 |              0.27 |             1361 |         25 |
| WmiPrvSE.exe                      |   212 |    119 |              0.22 |             2892 |         54 |

All processes: 60863 ms CPU, 112.9 % of one core.

## WSGM.exe

### Threads

|  PID |   TID | Name                             | CPU ms | Switches | Switches/s |
| ---: | ----: | -------------------------------- | -----: | -------: | ---------: |
| 4532 | 11580 |                                  |   1107 |      134 |          2 |
| 4532 | 12424 | .NET Long Running Task           |    761 |     7863 |        146 |
| 4532 | 10688 | .NET TP Worker                   |    621 |    13157 |        244 |
| 4532 | 13928 | .NET TP Worker                   |    507 |    11231 |        208 |
| 4532 | 11588 | .NET TP Worker                   |    488 |     8263 |        153 |
| 4532 |  8800 |                                  |    470 |    35842 |        665 |
| 4532 | 10116 | .NET TP Worker                   |    433 |     8781 |        163 |
| 4532 | 10280 |                                  |    403 |     8816 |        163 |
| 4532 | 14768 | .NET TP Worker                   |    365 |     5045 |         94 |
| 4532 |  4784 |                                  |    350 |     8012 |        149 |
| 4532 |  6500 |                                  |    317 |     7440 |        138 |
| 4532 | 12072 |                                  |    291 |     7336 |        136 |
| 4532 | 12012 |                                  |    222 |     3455 |         64 |
| 4532 | 11648 | .NET ThreadPool IO               |    101 |     3955 |         73 |
| 4532 |  9724 | .NET Tiered Compilation Worker   |     65 |       68 |          1 |
| 4532 | 13672 |                                  |     48 |      654 |         12 |
| 4532 |  8956 | .NET Timer                       |     39 |     1436 |         27 |
| 4532 |  9500 | DwmRenderTimerLoop               |     15 |      232 |          4 |
| 4532 | 10480 | MetricsEventSource CollectWorker |     10 |       36 |          1 |
| 4532 | 12940 |                                  |      8 |      124 |          2 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 4273 ms (64.3 %) `(no WSGM frame)`
- 1038 ms (15.6 %) `libviiper.dll!0x12411`
- 241 ms (3.6 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0`
- 172 ms (2.6 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadVector 0x0`
- 89 ms (1.3 %) `WSGM.dll!dynamicClass::IL_STUB_PInvoke 0x0`
- 82 ms (1.2 %) `WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0`
- 76 ms (1.1 %)
  `WindowsDeviceControl.dll!WindowsDeviceControl.WindowsRadio::ConnectedBluetoothCount 0x0`
- 72 ms (1.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawControllerSource+<ReadLoopAsync>d__15::MoveNext 0x0`
- 65 ms (1.0 %) `WSGM.dll!WSGM.Core.RtssNativeAdapter::ReadCore 0x0`
- 53 ms (0.8 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0`
- 48 ms (0.7 %) `WSGM.dll!WSGM.Shell.DevicePluginRuntime::Raise 0x0`
- 46 ms (0.7 %) `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.MsiWmiPlatform::InvokeCore 0x0`
- 36 ms (0.5 %) `WSGM.dll!WSGM.Shell.ControllerManager+<DrainSamplesAsync>d__60::MoveNext 0x0`
- 21 ms (0.3 %) `WSGM.dll!WSGM.Core.WindowFinder::FindProcessIds 0x0`
- 14 ms (0.2 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawIdentityReader::QuerySingle 0x0`
- 11 ms (0.2 %) `WSGM.dll!WSGM.Core.WindowsRtssDiscoveryEnvironment::ReadProcesses 0x0`
- 9 ms (0.1 %) `WSGM.dll!WSGM.Shell.RemovableDriveManager::ComputeSignature 0x0`
- 8 ms (0.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::Release 0x0`
- 7 ms (0.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.MsiWmiPlatform::FindActiveInstance 0x0`
- 6 ms (0.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.ControllerService::PublishControllerSampleAsync 0x0`

### CPU by leaf function

- 2852 ms (42.9 %) `?`
- 371 ms (5.6 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 300 ms (4.5 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 237 ms (3.6 %) `coreclr.dll!ThreadNative_SpinWait`
- 111 ms (1.7 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 56 ms (0.8 %) `ntoskrnl.exe!RtlpWalkFrameChain`
- 51 ms (0.8 %) `ntoskrnl.exe!RtlpLookupUserFunctionTableInverted`
- 51 ms (0.8 %) `ntoskrnl.exe!SwapContext`
- 49 ms (0.7 %) `ntoskrnl.exe!KiSystemServiceUser`
- 41 ms (0.6 %) `ntoskrnl.exe!ObpReferenceObjectByHandleWithTag`
- 36 ms (0.5 %) `ntoskrnl.exe!KiPageFault`
- 32 ms (0.5 %) `ntoskrnl.exe!EtwpLogKernelEvent`
- 29 ms (0.4 %) `ntoskrnl.exe!EtwpReserveTraceBuffer`
- 29 ms (0.4 %) `ntdll.dll!RtlpLowFragHeapAllocFromContext`
- 27 ms (0.4 %) `ntoskrnl.exe!ExAllocateHeapPool`
- 26 ms (0.4 %) `?!0x0`
- 26 ms (0.4 %) `ntoskrnl.exe!EtwpQueueApc`
- 26 ms (0.4 %) `ntoskrnl.exe!EtwpWriteUserEvent`
- 26 ms (0.4 %) `ntoskrnl.exe!RtlpLookupDynamicUserFunctionTable`
- 22 ms (0.3 %) `ntoskrnl.exe!KeSetTimer2`

### Hottest sampled stacks (leaf first)

- 2852 ms (42.9 %) `(no stack)`
- 326 ms (4.9 %) `(kernel only)`
- 195 ms (2.9 %)

  ```text
  coreclr.dll!ThreadNative_SpinWait
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 194 ms (2.9 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 175 ms (2.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CCC5
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

- 169 ms (2.5 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x53D4C
  libviiper.dll!0x558D7
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  libviiper.dll!0x89D38
  ```

- 132 ms (2.0 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x4745A
  libviiper.dll!0x5539F
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  libviiper.dll!0x89D38
  ```

- 111 ms (1.7 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 105 ms (1.6 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 73 ms (1.1 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadVector 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  ```

- 72 ms (1.1 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x49055
  libviiper.dll!0x1BDE5
  libviiper.dll!0x53FDC
  libviiper.dll!0x85008
  libviiper.dll!0x5655E
  libviiper.dll!0x56A67
  libviiper.dll!0x56E79
  libviiper.dll!0x89D38
  ```

- 70 ms (1.1 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  ```

- 50 ms (0.8 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::ReleaseCore 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::MaybeAddWorkingWorker 0x0
  System.Private.CoreLib.dll!System.Threading.ThreadPool::RequestWorkerThread 0x0
  System.Private.CoreLib.dll!System.Threading.ThreadPoolWorkQueue::Dispatch 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 44 ms (0.7 %)

  ```text
  ntdll.dll!NtSetTimerEx
  KernelBase.dll!SetWaitableTimer
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CC7B
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

- 42 ms (0.6 %)

  ```text
  coreclr.dll!ThreadNative_SpinWait
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 34 ms (0.5 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+IOCompletionPoller::Poll()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 31 ms (0.5 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 30 ms (0.5 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPRecv
  ws2_32.dll!WSARecv
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 28 ms (0.4 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 27 ms (0.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  KernelBase.dll!GetOverlappedResultEx
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 34697 switches (26.2 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CCC5
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

- 18772 switches (14.2 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 16917 switches (12.8 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x4745A
  libviiper.dll!0x5539F
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  libviiper.dll!0x89D38
  ```

- 14875 switches (11.2 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x53D4C
  libviiper.dll!0x558D7
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  ```

- 14439 switches (10.9 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 3952 switches (3.0 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+IOCompletionPoller::Poll()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3684 switches (2.8 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 3021 switches (2.3 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 2609 switches (2.0 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  coreclr.dll!Thread::DoAppropriateAptStateWait
  coreclr.dll!Thread::DoAppropriateWaitWorker
  coreclr.dll!Thread::DoAppropriateWait
  coreclr.dll!WaitHandle_WaitOneCore
  System.Private.CoreLib.dll!System.Threading.WaitHandle::WaitOne 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  System.Private.CoreLib.dll!System.Threading.Tasks.Task::ExecuteWithThreadLocal(System.Threading.Tasks.Task&, System.Threading.Thread)
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  ```

- 2605 switches (2.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  KernelBase.dll!GetOverlappedResultEx
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  ```

- 2605 switches (2.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  KernelBase.dll!GetOverlappedResultEx
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadVector 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  ```

- 1696 switches (1.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x53D4C
  libviiper.dll!0x5469E
  libviiper.dll!0x569AA
  libviiper.dll!0x56E79
  ```

- 1686 switches (1.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x54433
  libviiper.dll!0x5697A
  libviiper.dll!0x5707C
  libviiper.dll!0x66BDA
  ```

- 1537 switches (1.2 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  AudioSes.dll!<lambda_e089054b4a688316f1f807da67893f90>::operator()
  AudioSes.dll!CAudioClient::IsFormatSupported
  WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WindowsDeviceControl.dll!WindowsDeviceControl.CoreAudio::ListSupportedDeviceFormats 0x0
  WSGM.dll!WSGM.Shell.AudioProfileService::ReadPlaybackCapabilities 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task`1[System.__Canon]::InnerInvoke 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunFromThreadPoolDispatchLoop 0x0
  ```

- 1431 switches (1.1 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  coreclr.dll!Thread::DoAppropriateAptStateWait
  coreclr.dll!Thread::DoAppropriateWaitWorker
  coreclr.dll!Thread::DoAppropriateWait
  coreclr.dll!WaitHandle_WaitOneCore
  System.Private.CoreLib.dll!System.Threading.WaitHandle::WaitOneNoCheck 0x0
  System.Private.CoreLib.dll!System.Threading.TimerQueue::TimerThread()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  ```

- 1012 switches (0.8 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48F13
  libviiper.dll!0x1C046
  libviiper.dll!0x1C21F
  libviiper.dll!0x5B765
  libviiper.dll!0x526FD
  ```

- 641 switches (0.5 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SwitchToThread
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7FCE5
  libviiper.dll!0x5D9EC
  libviiper.dll!0x5DB37
  libviiper.dll!0x5606F
  libviiper.dll!0x54F05
  ```

- 347 switches (0.3 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x4745A
  libviiper.dll!0x5539F
  libviiper.dll!0x569F1
  libviiper.dll!0x577D8
  libviiper.dll!0x89D38
  ```

- 340 switches (0.3 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  powrprof.dll!UmpoRpcReadFromSystemPowerKey
  powrprof.dll!PowerReadPossibleValue
  WindowsDeviceControl.dll!WindowsDeviceControl.WindowsPower::PossibleValues 0x0
  WindowsDeviceControl.dll!WindowsDeviceControl.WindowsPower::QueryHybridCores 0x0
  WSGM.dll!WSGM.Core.HybridCores::Read 0x0
  WSGM.dll!WSGM.Shell.NativeQamHybridCoreService::<ReadAsync>b__6_0 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task`1[System.__Canon]::InnerInvoke 0x0
  ```

- 331 switches (0.3 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageW
  WSGM.dll!dynamicClass::IL_STUB_PInvoke 0x0
  Avalonia.Win32.dll!Avalonia.Win32.Win32DispatcherImpl::RunLoop 0x0
  Avalonia.Base.dll!Avalonia.Threading.DispatcherFrame::Run 0x0
  Avalonia.Base.dll!Avalonia.Threading.Dispatcher::PushFrame 0x0
  Avalonia.Base.dll!Avalonia.Threading.Dispatcher::MainLoop 0x0
  Avalonia.Controls.dll!Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime::StartCore 0x0
  Avalonia.Controls.dll!Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime::Start 0x0
  Avalonia.Controls.dll!Avalonia.ClassicDesktopStyleApplicationLifetimeExtensions::StartWithClassicDesktopLifetime 0x0
  WSGM.dll!WSGM.Program+<MainAsync>d__15::MoveNext 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncMethodBuilderCore::Start 0x0
  ```

### .NET exceptions thrown

- 32 thrown (100.0 %) `System.Threading.WaitHandleCannotBeOpenedException`

### .NET exception throw sites

- 32 thrown (100.0 %) `System.Threading.WaitHandleCannotBeOpenedException @ (no stack)`

### Thread 11580 : 1107 ms, 134 switches

### Thread 11580 hottest stacks

- 1104 ms (99.7 %) `(no stack)`
- 1 ms (0.1 %)

  ```text
  rpcrt4.dll!RpcBindingFree
  rpcrt4.dll!Ndr64UnmarshallHandle
  rpcrt4.dll!Ndr64SupplementUnmarshall
  rpcrt4.dll!Ndr64pClientUnMarshal
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  cfgmgr32.dll!TRpcQuery::WaitCallback
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (0.1 %) `(kernel only)`
- 1 ms (0.1 %)

  ```text
  ntdll.dll!RtlpLowFragHeapAllocFromContext
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  rpcrt4.dll!NdrpHandleAllocate
  rpcrt4.dll!MesDecodeIncrementalHandleCreate
  cfgmgr32.dll!TRpcQuery::WaitCallback
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 11580 wait sites

- 67 switches (50.0 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 64 switches (47.8 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  cfgmgr32.dll!TRpcQuery::WaitCallback
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.7 %)

  ```text
  ntdll.dll!ZwTraceControl
  sechost.dll!EnumerateTraceGuidsEx
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.EtwEventProvider::GetSessionInfo(System.Diagnostics.Tracing.EtwEventProvider+SessionInfoCallback, System.Collections.Generic.List`1[System.Diagnostics.Tracing.EtwEventProvider+SessionInfo]&)
  System.Private.CoreLib.dll!System.Collections.Generic.List`1[System.Collections.Generic.KeyValuePair`2[System.Diagnostics.Tracing.EtwEventProvider+SessionInfo, System.Boolean]] System.Diagnostics.Tracing.EtwEventProvider::GetChangedSessions()
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.EtwEventProvider::HandleEnableNotification(System.Diagnostics.Tracing.EventProvider, System.Byte*, System.Byte, System.Int64, System.Int64, Interop+Advapi32+EVENT_FILTER_DESCRIPTOR*)
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.EventProviderImpl::ProviderCallback(System.Diagnostics.Tracing.EventProvider, System.Byte*, System.Int32, System.Byte, System.Int64, System.Int64, Interop+Advapi32+EVENT_FILTER_DESCRIPTOR*)
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.EtwEventProvider::Callback(System.Guid*, System.Int32, System.Byte, System.Int64, System.Int64, Interop+Advapi32+EVENT_FILTER_DESCRIPTOR*, System.Void*)
  ntdll.dll!EtwpEventApiCallback
  ntdll.dll!EtwpUpdateEnableInfoAndCallback
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ```

- 1 switches (0.7 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  rpcrt4.dll!McGenEventWrite_EtwEventWriteTransfer
  rpcrt4.dll!I_RpcFreeBuffer
  rpcrt4.dll!Ndr64pClientFinally
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  cfgmgr32.dll!TRpcQuery::WaitCallback
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 12424 .NET Long Running Task: 761 ms, 7863 switches

### Thread 12424 hottest stacks

- 253 ms (33.2 %) `(no stack)`
- 73 ms (9.6 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadVector 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  ```

- 70 ms (9.2 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  ```

- 28 ms (3.7 %) `(kernel only)`

### Thread 12424 wait sites

- 2609 switches (33.2 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  coreclr.dll!Thread::DoAppropriateAptStateWait
  coreclr.dll!Thread::DoAppropriateWaitWorker
  coreclr.dll!Thread::DoAppropriateWait
  coreclr.dll!WaitHandle_WaitOneCore
  System.Private.CoreLib.dll!System.Threading.WaitHandle::WaitOne 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource::Produce 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object)
  System.Private.CoreLib.dll!System.Threading.Tasks.Task::ExecuteWithThreadLocal(System.Threading.Tasks.Task&, System.Threading.Thread)
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  ```

- 2605 switches (33.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  KernelBase.dll!GetOverlappedResultEx
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  ```

- 2605 switches (33.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  KernelBase.dll!GetOverlappedResultEx
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadVector 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  ```

- 12 switches (0.2 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  SensorsNativeApi.V2.dll!SensorDeviceIoControlWithTimeoutV2
  SensorsNativeApi.V2.dll!SensorGetCurrentReadingV2
  SensorsApi.dll!<lambda_f910444eeaaa7a05060baaa10ea55434>::operator()
  SensorsApi.dll!wil::details::functor_wrapper_void<<lambda_f910444eeaaa7a05060baaa10ea55434> >::Run
  SensorsApi.dll!wil::details::ResultFromException
  SensorsApi.dll!CSensorV2::GetData
  System.Management!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadGyrometer 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryRead 0x0
  ```

### Thread 10688 .NET TP Worker: 621 ms, 13157 switches

### Thread 10688 hottest stacks

- 116 ms (18.7 %) `(no stack)`
- 66 ms (10.6 %)

  ```text
  coreclr.dll!ThreadNative_SpinWait
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 39 ms (6.3 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 38 ms (6.1 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 10688 wait sites

- 6617 switches (50.3 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 4581 switches (34.8 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 544 switches (4.1 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  AudioSes.dll!<lambda_e089054b4a688316f1f807da67893f90>::operator()
  AudioSes.dll!CAudioClient::IsFormatSupported
  WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WindowsDeviceControl.dll!WindowsDeviceControl.CoreAudio::ListSupportedDeviceFormats 0x0
  WSGM.dll!WSGM.Shell.AudioProfileService::ReadPlaybackCapabilities 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task`1[System.__Canon]::InnerInvoke 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunFromThreadPoolDispatchLoop 0x0
  ```

- 515 switches (3.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x54433
  libviiper.dll!0x5697A
  libviiper.dll!0x5707C
  libviiper.dll!0x66BDA
  ```

### Thread 13928 .NET TP Worker: 507 ms, 11231 switches

### Thread 13928 hottest stacks

- 104 ms (20.5 %) `(no stack)`
- 54 ms (10.6 %)

  ```text
  coreclr.dll!ThreadNative_SpinWait
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 34 ms (6.7 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 33 ms (6.5 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 13928 wait sites

- 5411 switches (48.2 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 4465 switches (39.8 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 417 switches (3.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x54433
  libviiper.dll!0x5697A
  libviiper.dll!0x5707C
  libviiper.dll!0x66BDA
  ```

- 270 switches (2.4 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  AudioSes.dll!<lambda_e089054b4a688316f1f807da67893f90>::operator()
  AudioSes.dll!CAudioClient::IsFormatSupported
  WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WindowsDeviceControl.dll!WindowsDeviceControl.CoreAudio::ListSupportedDeviceFormats 0x0
  WSGM.dll!WSGM.Shell.AudioProfileService::ReadPlaybackCapabilities 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task`1[System.__Canon]::InnerInvoke 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunFromThreadPoolDispatchLoop 0x0
  ```

### Thread 11588 .NET TP Worker: 488 ms, 8263 switches

### Thread 11588 hottest stacks

- 120 ms (24.7 %) `(no stack)`
- 42 ms (8.6 %)

  ```text
  coreclr.dll!ThreadNative_SpinWait
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 28 ms (5.7 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 25 ms (5.1 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 11588 wait sites

- 3684 switches (44.6 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 3021 switches (36.6 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback 0x0
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 450 switches (5.4 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  AudioSes.dll!<lambda_e089054b4a688316f1f807da67893f90>::operator()
  AudioSes.dll!CAudioClient::IsFormatSupported
  WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WindowsDeviceControl.dll!WindowsDeviceControl.CoreAudio::ListSupportedDeviceFormats 0x0
  WSGM.dll!WSGM.Shell.AudioProfileService::ReadPlaybackCapabilities 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task`1[System.__Canon]::InnerInvoke 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunFromThreadPoolDispatchLoop 0x0
  ```

- 272 switches (3.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x54433
  libviiper.dll!0x5697A
  libviiper.dll!0x5707C
  libviiper.dll!0x66BDA
  ```

### Thread 8800 : 470 ms, 35842 switches

### Thread 8800 hottest stacks

- 175 ms (37.2 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CCC5
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

- 127 ms (27.0 %) `(no stack)`
- 68 ms (14.5 %) `(kernel only)`
- 44 ms (9.4 %)

  ```text
  ntdll.dll!NtSetTimerEx
  KernelBase.dll!SetWaitableTimer
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CC7B
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

### Thread 8800 wait sites

- 34697 switches (96.8 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CCC5
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

- 1012 switches (2.8 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48F13
  libviiper.dll!0x1C046
  libviiper.dll!0x1C21F
  libviiper.dll!0x5B765
  libviiper.dll!0x526FD
  ```

- 65 switches (0.2 %)

  ```text
  ntdll.dll!NtGetContextThread
  KernelBase.dll!GetThreadContext
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x4A69D
  libviiper.dll!0x5C111
  libviiper.dll!0x5BAF8
  libviiper.dll!0x5B93A
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  ```

- 12 switches (0.0 %)

  ```text
  KernelBase.dll!WaitForSingleObject
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7CCC5
  libviiper.dll!0x5B612
  libviiper.dll!0x526FD
  libviiper.dll!0x5264A
  libviiper.dll!0x89CAA
  libviiper.dll!0x8D277
  ```

### Thread 10116 .NET TP Worker: 433 ms, 8781 switches

### Thread 10116 hottest stacks

- 149 ms (34.3 %) `(no stack)`
- 46 ms (10.6 %)

  ```text
  coreclr.dll!ThreadNative_SpinWait
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 25 ms (5.8 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 24 ms (5.6 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 10116 wait sites

- 4104 switches (46.7 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 3539 switches (40.3 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::Wait 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  ```

- 297 switches (3.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x54433
  libviiper.dll!0x5697A
  libviiper.dll!0x5707C
  libviiper.dll!0x66BDA
  ```

- 181 switches (2.1 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  AudioSes.dll!<lambda_e089054b4a688316f1f807da67893f90>::operator()
  AudioSes.dll!CAudioClient::IsFormatSupported
  WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0
  WindowsDeviceControl.dll!WindowsDeviceControl.CoreAudio::ListSupportedDeviceFormats 0x0
  WSGM.dll!WSGM.Shell.AudioProfileService::ReadPlaybackCapabilities 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task`1[System.__Canon]::InnerInvoke 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunFromThreadPoolDispatchLoop 0x0
  ```

### Thread 10280 : 403 ms, 8816 switches

### Thread 10280 hottest stacks

- 147 ms (36.4 %) `(no stack)`
- 61 ms (15.2 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 47 ms (11.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x53D4C
  libviiper.dll!0x558D7
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  libviiper.dll!0x89D38
  ```

- 37 ms (9.2 %) `(kernel only)`

### Thread 10280 wait sites

- 4389 switches (49.8 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x4745A
  libviiper.dll!0x5539F
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  libviiper.dll!0x89D38
  ```

- 3626 switches (41.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x53D4C
  libviiper.dll!0x558D7
  libviiper.dll!0x569F1
  libviiper.dll!0x56E79
  ```

- 410 switches (4.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x48E45
  libviiper.dll!0x1BEA5
  libviiper.dll!0x53D4C
  libviiper.dll!0x5469E
  libviiper.dll!0x569AA
  libviiper.dll!0x56E79
  ```

- 151 switches (1.7 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SwitchToThread
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6EE
  libviiper.dll!0x49D3A
  libviiper.dll!0x7FCE5
  libviiper.dll!0x5D9EC
  libviiper.dll!0x5DB37
  libviiper.dll!0x5606F
  libviiper.dll!0x54F05
  ```

## WSGM.LogonService.exe

### Threads

|  PID |   TID | Name                           | CPU ms | Switches | Switches/s |
| ---: | ----: | ------------------------------ | -----: | -------: | ---------: |
| 2384 | 14496 |                                |      5 |       19 |          0 |
| 2384 | 11772 | .NET Tiered Compilation Worker |      2 |        0 |          0 |
| 2384 |  9124 | .NET Tiered Compilation Worker |      1 |        0 |          0 |
| 2384 |  3948 | .NET Tiered Compilation Worker |      1 |        6 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 8 ms (89.1 %) `(no WSGM frame)`
- 1 ms (10.9 %) `WSGM.LogonService.exe!CEEInfo::resolveToken`

### CPU by leaf function

- 8 ms (89.1 %) `?`
- 1 ms (10.9 %) `WSGM.LogonService.exe!CEEInfo::resolveToken`

### Hottest sampled stacks (leaf first)

- 8 ms (89.1 %) `(no stack)`
- 1 ms (10.9 %)

  ```text
  WSGM.LogonService.exe!CEEInfo::resolveToken
  WSGM.LogonService.exe!Compiler::impImportBlockCode
  WSGM.LogonService.exe!Compiler::impImportBlock
  WSGM.LogonService.exe!Compiler::impImport
  WSGM.LogonService.exe!Compiler::fgImport
  WSGM.LogonService.exe!DoPhase
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  WSGM.LogonService.exe!UnsafeJitFunction
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLocked
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 8 switches (32.0 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  WSGM.LogonService.exe!McGenEventWrite_EventWriteTransfer
  WSGM.LogonService.exe!ETW::GCLog::GCSettingsEvent
  WSGM.LogonService.exe!EtwCallback
  WSGM.LogonService.exe!McGenControlCallbackV2
  ntdll.dll!EtwpEventApiCallback
  ntdll.dll!EtwpUpdateEnableInfoAndCallback
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  ```

- 4 switches (16.0 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 switches (12.0 %)

  ```text
  ntdll.dll!RtlpLowFragHeapAllocFromContext
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  ntdll.dll!RtlFlsSetValue
  KernelBase.dll!FlsSetValue
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (8.0 %) `ntdll.dll!RtlUserThreadStart`
- 2 switches (8.0 %) `(kernel only)`
- 2 switches (8.0 %)

  ```text
  ntdll.dll!memcmp
  ntdll.dll!EtwpFindRegistration
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (4.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  WSGM.LogonService.exe!CLREventWaitHelper
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  WSGM.LogonService.exe!ManagedThreadBase_DispatchOuter
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (4.0 %)

  ```text
  msvcrt.dll!_initptd
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 1 switches (4.0 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  WSGM.LogonService.exe!ManagedThreadBase_DispatchOuter
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (4.0 %)

  ```text
  WSGM.LogonService.exe!MethodDesc::Helper_IsEligibleForVersioningWithVtableSlotBackpatch
  WSGM.LogonService.exe!TieredCompilationManager::ActivateCodeVersion
  WSGM.LogonService.exe!TieredCompilationManager::DoBackgroundWork
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  WSGM.LogonService.exe!ManagedThreadBase_DispatchOuter
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 14496 : 5 ms, 19 switches

### Thread 14496 hottest stacks

- 5 ms (100.0 %) `(no stack)`

### Thread 14496 wait sites

- 8 switches (42.1 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  WSGM.LogonService.exe!McGenEventWrite_EventWriteTransfer
  WSGM.LogonService.exe!ETW::GCLog::GCSettingsEvent
  WSGM.LogonService.exe!EtwCallback
  WSGM.LogonService.exe!McGenControlCallbackV2
  ntdll.dll!EtwpEventApiCallback
  ntdll.dll!EtwpUpdateEnableInfoAndCallback
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  ```

- 4 switches (21.1 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 switches (15.8 %)

  ```text
  ntdll.dll!RtlpLowFragHeapAllocFromContext
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  ntdll.dll!RtlFlsSetValue
  KernelBase.dll!FlsSetValue
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (10.5 %)

  ```text
  ntdll.dll!memcmp
  ntdll.dll!EtwpFindRegistration
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 11772 .NET Tiered Compilation Worker: 2 ms, 0 switches

### Thread 11772 hottest stacks

- 2 ms (100.0 %) `(no stack)`

### Thread 9124 .NET Tiered Compilation Worker: 1 ms, 0 switches

### Thread 9124 hottest stacks

- 1 ms (100.0 %) `(no stack)`

### Thread 3948 .NET Tiered Compilation Worker: 1 ms, 6 switches

### Thread 3948 hottest stacks

- 1 ms (100.0 %)

  ```text
  WSGM.LogonService.exe!CEEInfo::resolveToken
  WSGM.LogonService.exe!Compiler::impImportBlockCode
  WSGM.LogonService.exe!Compiler::impImportBlock
  WSGM.LogonService.exe!Compiler::impImport
  WSGM.LogonService.exe!Compiler::fgImport
  WSGM.LogonService.exe!DoPhase
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  WSGM.LogonService.exe!UnsafeJitFunction
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLocked
  ```

### Thread 3948 wait sites

- 2 switches (33.3 %) `(kernel only)`
- 1 switches (16.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  WSGM.LogonService.exe!CLREventWaitHelper
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  WSGM.LogonService.exe!ManagedThreadBase_DispatchOuter
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (16.7 %) `ntdll.dll!RtlUserThreadStart`
- 1 switches (16.7 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  WSGM.LogonService.exe!ManagedThreadBase_DispatchOuter
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

## steam.exe

### Threads

|  PID |   TID | Name                            | CPU ms | Switches | Switches/s |
| ---: | ----: | ------------------------------- | -----: | -------: | ---------: |
| 8660 |  9976 |                                 |    176 |     2045 |         38 |
| 8660 | 11388 | CSteamController::CHIDIO Thread |    169 |     1951 |         36 |
| 8660 |  5604 | IPC:CSteamEngine                |     99 |     1157 |         21 |
| 8660 |  2672 | CHTTPClientThreadPool:0         |      8 |      171 |          3 |
| 8660 | 11948 |                                 |      7 |      223 |          4 |
| 8660 | 13584 | CSystemManager::m_pWorkThreadPo |      7 |      154 |          3 |
| 8660 |  9100 |                                 |      7 |      202 |          4 |
| 8660 |  5668 | CHTTPClientThreadPool:0         |      6 |      158 |          3 |
| 8660 | 12588 | Controller Work Item Thread     |      5 |       33 |          1 |
| 8660 |  8988 | CJobMgr::m_WorkThreadPool:1     |      5 |      149 |          3 |
| 8660 |  4692 | IOCP Thread 0                   |      5 |       88 |          2 |
| 8660 |  9580 | CJobMgr::m_WorkThreadPool:0     |      4 |      163 |          3 |
| 8660 | 11984 | CJobMgr::m_WorkThreadPool:1     |      4 |      160 |          3 |
| 8660 | 13032 | CJobMgr::m_WorkThreadPool:0     |      4 |      155 |          3 |
| 8660 |  2264 | IOCP Thread 0                   |      3 |       83 |          2 |
| 8660 |  9256 |                                 |      3 |      209 |          4 |
| 8660 |  6260 | CHTTPCacheFileThreadPool:0      |      2 |      144 |          3 |
| 8660 |  8112 | CNet Encrypt:0                  |      2 |      153 |          3 |
| 8660 | 15208 | CHTTPClientThreadPool:0         |      1 |      153 |          3 |
| 8660 |   340 | SteamUIWatchdogThread           |      1 |        9 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 522 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 181 ms (34.6 %) `?`
- 37 ms (7.1 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 13 ms (2.5 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 8 ms (1.5 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 6 ms (1.1 %) `tier0_s64.dll!0x1322F`
- 6 ms (1.1 %) `ntoskrnl.exe!RtlpLookupUserFunctionTableInverted`
- 5 ms (1.0 %) `ntoskrnl.exe!KeQueryPerformanceCounter`
- 5 ms (1.0 %) `ntoskrnl.exe!ObpReferenceObjectByHandleWithTag`
- 5 ms (1.0 %) `ntoskrnl.exe!KiCommitThreadWait`
- 4 ms (0.8 %) `ntoskrnl.exe!RtlpWalkFrameChain`
- 3 ms (0.6 %) `steamclient64.dll!0xD26613`
- 3 ms (0.6 %) `ntdll.dll!RtlQueryPerformanceCounter`
- 2 ms (0.4 %) `ntoskrnl.exe!KiSystemServiceUser`
- 2 ms (0.4 %) `tier0_s64.dll!0xEE80`
- 2 ms (0.4 %) `ntoskrnl.exe!IoGetRelatedDeviceObject`
- 2 ms (0.4 %) `steamclient64.dll!0x614C80`
- 2 ms (0.4 %) `steamclient64.dll!0x712A04`
- 2 ms (0.4 %) `tier0_s64.dll!0x1AD08`
- 2 ms (0.4 %) `ntoskrnl.exe!KiDeferredReadySingleThread`
- 2 ms (0.4 %) `ntoskrnl.exe!ExAllocateHeapPool`

### Hottest sampled stacks (leaf first)

- 181 ms (34.6 %) `(no stack)`
- 26 ms (5.0 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageW
  vgui2_s.dll!0xB9E11
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  kernel32.dll!BaseThreadInitThunk
  ```

- 22 ms (4.2 %) `(kernel only)`
- 14 ms (2.7 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  steamclient64.dll!0x614AC8
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 14 ms (2.7 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SDL3.dll!0xD76A9
  steamclient64.dll!0x67985B
  steamclient64.dll!0x60D710
  steamclient64.dll!0x614243
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 13 ms (2.5 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  SDL3.dll!0x108561
  SDL3.dll!0x11712A
  steamclient64.dll!0x6D1B83
  steamclient64.dll!0x61393A
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  ```

- 11 ms (2.1 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  tier0_s64.dll!0x12F0D
  steamclient64.dll!0x89E122
  steamclient64.dll!0x8A009D
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

- 9 ms (1.7 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  vgui2_s.dll!0xB9E59
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 8 ms (1.5 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 6 ms (1.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  SteamUI.dll!0x915D99
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 4 ms (0.8 %)

  ```text
  win32u.dll!ZwUserGetAncestor
  vgui2_s.dll!0xA56FE
  vgui2_s.dll!0x7574C
  vgui2_s.dll!0x7858B
  vgui2_s.dll!0xC67B1
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 3 ms (0.6 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  hid.dll!DeviceIoControlHelper
  hid.dll!HidD_SetFeature
  SDL3.dll!0xD3602
  steamclient64.dll!0x67DE08
  steamclient64.dll!0x67E9E2
  steamclient64.dll!0x615C02
  steamclient64.dll!0x6131E1
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  ```

- 3 ms (0.6 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  steamclient64.dll!0x6CB344
  steamclient64.dll!0x6CF21E
  steamclient64.dll!0x61395F
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

- 3 ms (0.6 %)

  ```text
  win32u.dll!ZwUserShowWindow
  vgui2_s.dll!0xAEED7
  vgui2_s.dll!0xC6966
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (0.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  SteamUI.dll!0x92B99B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (0.6 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x98523D
  steamclient64.dll!0x89FF64
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (0.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0xEA364
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (0.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0x127C57
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (0.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xE6BC1B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 ms (0.4 %)

  ```text
  steamclient64.dll!0xD26613
  steamclient64.dll!0x4C34D4
  steamclient64.dll!0x9DBA5B
  steamclient64.dll!0x4D1DB0
  steamclient64.dll!0x4E4285
  steamclient64.dll!0x4E3BE4
  steamclient64.dll!0x9EF9E5
  steamclient64.dll!0x9859ED
  steamclient64.dll!0x89FF64
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 1269 switches (16.1 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  steamclient64.dll!0x614AC8
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1171 switches (14.8 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 972 switches (12.3 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageW
  vgui2_s.dll!0xB9E11
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  ```

- 693 switches (8.8 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  tier0_s64.dll!0x12F0D
  steamclient64.dll!0x89E122
  steamclient64.dll!0x8A009D
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

- 468 switches (5.9 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  SDL3.dll!0x108561
  SDL3.dll!0x11712A
  steamclient64.dll!0x6D1B83
  steamclient64.dll!0x61393A
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  ```

- 290 switches (3.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  SteamUI.dll!0x915D99
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 283 switches (3.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0x127C57
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 233 switches (3.0 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  vgui2_s.dll!0xB9E59
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  ```

- 233 switches (3.0 %)

  ```text
  win32u.dll!ZwUserShowWindow
  vgui2_s.dll!0xAEEED
  vgui2_s.dll!0xC6966
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 205 switches (2.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0xEA364
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 203 switches (2.6 %)

  ```text
  win32u.dll!ZwUserGetAncestor
  vgui2_s.dll!0xA56FE
  vgui2_s.dll!0x7574C
  vgui2_s.dll!0x7858B
  vgui2_s.dll!0xC67B1
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  ```

- 197 switches (2.5 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  steamclient64.dll!0x6CB344
  steamclient64.dll!0x6CF21E
  steamclient64.dll!0x61395F
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

- 192 switches (2.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xE6BC1B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 192 switches (2.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  SteamUI.dll!0x92B99B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 151 switches (1.9 %)

  ```text
  win32u.dll!ZwUserShowWindow
  vgui2_s.dll!0xAEED7
  vgui2_s.dll!0xC6966
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 86 switches (1.1 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  steamclient64.dll!0xEBCFC7
  steamclient64.dll!0xEBCD77
  steamclient64.dll!0xE90FB4
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  steamclient64.dll!0xE91267
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 82 switches (1.0 %)

  ```text
  ntdll.dll!NtRemoveIoCompletionEx
  KernelBase.dll!GetQueuedCompletionStatusEx
  SteamUI.dll!0x93BC77
  SteamUI.dll!0x93BA27
  SteamUI.dll!0x92A814
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  SteamUI.dll!0x92AAC7
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 75 switches (1.0 %)

  ```text
  win32u.dll!ZwUserGetCursorPos
  SteamUI.dll!0x6C33E2
  SteamUI.dll!0x6CDCE5
  SteamUI.dll!0x596DFA
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  kernel32.dll!BaseThreadInitThunk
  ```

- 55 switches (0.7 %)

  ```text
  win32u.dll!ZwUserGetAncestor
  vgui2_s.dll!0xB9CDE
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 44 switches (0.6 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  SteamUI.dll!0x8505DD
  SteamUI.dll!0x52E836
  SteamUI.dll!0x63B7E9
  SteamUI.dll!0x603F67
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  ```

### Thread 9976 : 176 ms, 2045 switches

### Thread 9976 hottest stacks

- 68 ms (38.4 %) `(no stack)`
- 26 ms (14.9 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageW
  vgui2_s.dll!0xB9E11
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  kernel32.dll!BaseThreadInitThunk
  ```

- 9 ms (5.1 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  vgui2_s.dll!0xB9E59
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 6 ms (3.4 %) `(kernel only)`

### Thread 9976 wait sites

- 972 switches (47.5 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageW
  vgui2_s.dll!0xB9E11
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  ```

- 233 switches (11.4 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  vgui2_s.dll!0xB9E59
  vgui2_s.dll!0xC66B2
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  ```

- 233 switches (11.4 %)

  ```text
  win32u.dll!ZwUserShowWindow
  vgui2_s.dll!0xAEEED
  vgui2_s.dll!0xC6966
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  steam.exe!0x831FE
  steam.exe!0x1F168A
  ```

- 203 switches (9.9 %)

  ```text
  win32u.dll!ZwUserGetAncestor
  vgui2_s.dll!0xA56FE
  vgui2_s.dll!0x7574C
  vgui2_s.dll!0x7858B
  vgui2_s.dll!0xC67B1
  SteamUI.dll!0x592EB7
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD0B2
  steam.exe!0x82D3F
  ```

### Thread 11388 CSteamController::CHIDIO Thread: 169 ms, 1951 switches

### Thread 11388 hottest stacks

- 58 ms (34.3 %) `(no stack)`
- 14 ms (8.3 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  steamclient64.dll!0x614AC8
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 14 ms (8.2 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SDL3.dll!0xD76A9
  steamclient64.dll!0x67985B
  steamclient64.dll!0x60D710
  steamclient64.dll!0x614243
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 13 ms (7.8 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  SDL3.dll!0x108561
  SDL3.dll!0x11712A
  steamclient64.dll!0x6D1B83
  steamclient64.dll!0x61393A
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  ```

### Thread 11388 wait sites

- 1269 switches (65.0 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  steamclient64.dll!0x614AC8
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 468 switches (24.0 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  SDL3.dll!0x108561
  SDL3.dll!0x11712A
  steamclient64.dll!0x6D1B83
  steamclient64.dll!0x61393A
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  ```

- 197 switches (10.1 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  SDL3.dll!0xD7E87
  steamclient64.dll!0x6CB344
  steamclient64.dll!0x6CF21E
  steamclient64.dll!0x61395F
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

- 3 switches (0.2 %)

  ```text
  SDL3.dll!0x8C990
  steamclient64.dll!0x6D1B83
  steamclient64.dll!0x61393A
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 5604 IPC:CSteamEngine: 99 ms, 1157 switches

### Thread 5604 hottest stacks

- 36 ms (36.4 %) `(no stack)`
- 11 ms (11.1 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  tier0_s64.dll!0x12F0D
  steamclient64.dll!0x89E122
  steamclient64.dll!0x8A009D
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

- 3 ms (3.0 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x98523D
  steamclient64.dll!0x89FF64
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (2.9 %) `(kernel only)`

### Thread 5604 wait sites

- 693 switches (59.9 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  tier0_s64.dll!0x12F0D
  steamclient64.dll!0x89E122
  steamclient64.dll!0x8A009D
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

- 20 switches (1.7 %)

  ```text
  win32u.dll!ZwUserGetCursorPos
  steamclient64.dll!0x987525
  steamclient64.dll!0x9857BA
  steamclient64.dll!0x89FF64
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

- 15 switches (1.3 %)

  ```text
  ntdll.dll!ZwClearEvent
  KernelBase.dll!ResetEvent
  tier0_s64.dll!0x1137E
  steamclient64.dll!0x89FF4A
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

- 15 switches (1.3 %)

  ```text
  steamclient64.dll!0x59CFF7
  steamclient64.dll!0x504B0E
  steamclient64.dll!0x9EF988
  steamclient64.dll!0x9859ED
  steamclient64.dll!0x89FF64
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  ```

### Thread 2672 CHTTPClientThreadPool:0: 8 ms, 171 switches

### Thread 2672 hottest stacks

- 2 ms (25.0 %) `(kernel only)`
- 2 ms (25.0 %) `(no stack)`
- 2 ms (25.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (12.5 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0xD907DF
  steamclient64.dll!0xD91563
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 2672 wait sites

- 163 switches (95.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.2 %)

  ```text
  steamclient64.dll!0xD905F7
  steamclient64.dll!0xD912A3
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.6 %)

  ```text
  steamclient64.dll!0xD917BD
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.6 %)

  ```text
  kernel32.dll!WaitForSingleObject
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 11948 : 7 ms, 223 switches

### Thread 11948 hottest stacks

- 3 ms (42.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0xEA364
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %)

  ```text
  steam.exe!0x1DBCCD
  steam.exe!0xEA314
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %) `(kernel only)`
- 1 ms (14.3 %) `(no stack)`

### Thread 11948 wait sites

- 205 switches (91.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0xEA364
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 5 switches (2.2 %)

  ```text
  steam.exe!0x1DBCCD
  steam.exe!0xEA314
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 switches (1.3 %)

  ```text
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0xEA364
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 switches (1.3 %)

  ```text
  steam.exe!0xEA300
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 13584 CSystemManager::m_pWorkThreadPo: 7 ms, 154 switches

### Thread 13584 hottest stacks

- 2 ms (28.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %)

  ```text
  steamclient64.dll!0xD917AE
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %) `(no stack)`
- 1 ms (14.3 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0xD907DF
  steamclient64.dll!0xD91563
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 13584 wait sites

- 144 switches (93.5 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xD917A9
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.3 %)

  ```text
  steamclient64.dll!0xD917BD
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.6 %)

  ```text
  steamclient64.dll!0xEC6D0
  steamclient64.dll!0xD916CC
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.6 %)

  ```text
  ntdll.dll!NtQueryWnfStateData
  kernel32.dll!GetSystemPowerStatus
  SDL3.dll!0x18EB63
  SDL3.dll!0x127FDF
  steamclient64.dll!0x9B1036
  steamclient64.dll!0x9B6137
  steamclient64.dll!0xD913DB
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

### Thread 9100 : 7 ms, 202 switches

### Thread 9100 hottest stacks

- 3 ms (42.9 %) `(no stack)`
- 3 ms (42.8 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xE6BC1B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %)

  ```text
  kernel32.dll!WaitForSingleObject
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xE6BC1B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 9100 wait sites

- 192 switches (95.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0xE6BC1B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.0 %)

  ```text
  tier0_s64.dll!0x1322F
  tier0_s64.dll!0x139AE
  steamclient64.dll!0xE6BC56
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.0 %)

  ```text
  steamclient64.dll!0xE6BC1F
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.5 %)

  ```text
  steamclient64.dll!0xE6BBE6
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 5668 CHTTPClientThreadPool:0: 6 ms, 158 switches

### Thread 5668 hottest stacks

- 3 ms (50.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0x127C57
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (16.7 %)

  ```text
  steam.exe!0x127730
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (16.7 %)

  ```text
  steam.exe!0x127C6A
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (16.7 %) `(kernel only)`

### Thread 5668 wait sites

- 149 switches (94.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steam.exe!0x1DF3E6
  steam.exe!0x127C57
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.3 %)

  ```text
  steam.exe!0x127730
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.6 %)

  ```text
  steam.exe!0x127C5C
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.6 %)

  ```text
  steam.exe!0x7E800
  steam.exe!0x127B82
  steam.exe!0x1DEF0F
  steam.exe!0x1E0BAF
  steam.exe!0x1E0C39
  steam.exe!0x1E0482
  steam.exe!0x1E135F
  steam.exe!0x1DF11D
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 9256 : 3 ms, 209 switches

### Thread 9256 hottest stacks

- 3 ms (100.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  SteamUI.dll!0x92B99B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 9256 wait sites

- 192 switches (91.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  SteamUI.dll!0x92B99B
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 4 switches (1.9 %)

  ```text
  tier0_s64.dll!0x13980
  SteamUI.dll!0x92B9D6
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.0 %)

  ```text
  SteamUI.dll!0x92B931
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.0 %)

  ```text
  SteamUI.dll!0x92B99F
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

## steamwebhelper.exe

### Threads

|   PID |   TID | Name                         | CPU ms | Switches | Switches/s |
| ----: | ----: | ---------------------------- | -----: | -------: | ---------: |
|  1648 |  8056 | CrRendererMain               |    492 |      722 |         13 |
|  3288 | 12692 | CrBrowserMain                |    343 |     3641 |         68 |
|  7020 |  8180 | StackSamplingProfiler        |     39 |      600 |         11 |
| 10784 |  9920 | network.CrUtilityMain        |     12 |       60 |          1 |
| 13104 | 12128 | CrGpuMain                    |     12 |       14 |          0 |
|  3288 |  7576 | ThreadPoolForegroundWorker   |     10 |       58 |          1 |
|  3288 |  9876 | Chrome_IOThread              |      9 |      114 |          2 |
|  3288 |  3768 | ThreadPoolServiceThread      |      7 |       86 |          2 |
|  3288 |  9416 | Chrome_DevToolsHandlerThread |      6 |       46 |          1 |
|  7020 |  2364 | Compositor                   |      3 |      331 |          6 |
| 10784 | 13984 | Chrome_ChildIOThread         |      3 |       31 |          1 |
|  6836 |  7416 | storage.CrUtilityMain        |      3 |       25 |          0 |
| 10784 | 12564 | ThreadPoolForegroundWorker   |      2 |       12 |          0 |
|  3288 |  1440 |                              |      2 |       39 |          1 |
|  1648 | 14020 | ThreadPoolForegroundWorker   |      2 |        9 |          0 |
| 10784 | 11592 | ThreadPoolForegroundWorker   |      2 |       10 |          0 |
|  4184 | 14064 |                              |      1 |        2 |          0 |
|  3288 |  1264 |                              |      1 |       19 |          0 |
|  1648 |  7900 | Chrome_ChildIOThread         |      1 |       71 |          1 |
|  7020 |  8604 | CrRendererMain               |      1 |        2 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 957 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 327 ms (34.2 %) `?`
- 83 ms (8.7 %) `?!0x0`
- 58 ms (6.1 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 25 ms (2.6 %) `libcef.dll!0x142076B`
- 13 ms (1.4 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 9 ms (0.9 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 7 ms (0.7 %) `ntoskrnl.exe!RtlpLookupUserFunctionTableInverted`
- 5 ms (0.5 %) `libcef.dll!0x32E73BE`
- 4 ms (0.4 %) `ntoskrnl.exe!ExpLookupHandleTableEntry`
- 4 ms (0.4 %) `ntoskrnl.exe!KiInsertTimerTable`
- 4 ms (0.4 %) `SDL3.dll!0x1AF39A`
- 4 ms (0.4 %) `ntoskrnl.exe!SwapContext`
- 3 ms (0.3 %) `libcef.dll!0x3402046`
- 3 ms (0.3 %) `ntoskrnl.exe!KiTransitionSchedulingGroupGeneration`
- 3 ms (0.3 %) `libcef.dll!0x3339514`
- 3 ms (0.3 %) `ntoskrnl.exe!RtlpWalkFrameChain`
- 3 ms (0.3 %) `steamwebhelper.exe!0x173D26`
- 3 ms (0.3 %) `libcef.dll!0x3343980`
- 3 ms (0.3 %) `kernel32.dll!QueryPerformanceCounterStub`
- 3 ms (0.3 %) `ntoskrnl.exe!ExAllocateHeapPool`

### Hottest sampled stacks (leaf first)

- 327 ms (34.2 %) `(no stack)`
- 80 ms (8.4 %)

  ```text
  win32u.dll!ZwUserMsgWaitForMultipleObjectsEx
  libcef.dll!0x34FDAAD
  libcef.dll!0x34FD694
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 30 ms (3.1 %) `(kernel only)`
- 19 ms (2.0 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2761A4
  ```

- 18 ms (1.9 %)

  ```text
  win32u.dll!ZwUserGetQueueStatus
  libcef.dll!0x13D2AD2
  libcef.dll!0x34FD6B3
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 18 ms (1.9 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2761A4
  libcef.dll!0x2B0087B
  ```

- 15 ms (1.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18C77AF
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  libcef.dll!0x338734
  libcef.dll!0x31DB09
  ```

- 11 ms (1.1 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2D2B5E4
  libcef.dll!0x2BB8A94
  libcef.dll!0x2BB8E44
  libcef.dll!0x2C4334E
  libcef.dll!0x2BB5F5C
  ```

- 10 ms (1.0 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FD623
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  ```

- 9 ms (0.9 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2761A4
  libcef.dll!0x2B0087B
  libcef.dll!0x28A3C7D
  ```

- 9 ms (0.9 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  ```

- 7 ms (0.7 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  ```

- 6 ms (0.6 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2761A4
  libcef.dll!0x2B0087B
  libcef.dll!0x28A3C7D
  libcef.dll!0x38F8453
  ```

- 5 ms (0.5 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ```

- 4 ms (0.4 %)

  ```text
  SDL3.dll!0x1AF39A
  SDL3.dll!0x970D6
  steamwebhelper.exe!0x173C65
  steamwebhelper.exe!0x2DA55C
  steamwebhelper.exe!0x2E6C08
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FD623
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  ```

- 4 ms (0.4 %)

  ```text
  ntdll.dll!NtResumeThread
  KernelBase.dll!ResumeThread
  libcef.dll!0x82013A7
  libcef.dll!0x8201840
  libcef.dll!0x82019E3
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  ```

- 4 ms (0.4 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  ```

- 4 ms (0.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (0.3 %)

  ```text
  steamwebhelper.exe!0x173D26
  steamwebhelper.exe!0x2DA55C
  steamwebhelper.exe!0x2E6C08
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FD623
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  ```

- 3 ms (0.3 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  libcef.dll!0x13D2BE8
  libcef.dll!0x34FD6B3
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 2691 switches (44.7 %)

  ```text
  win32u.dll!ZwUserMsgWaitForMultipleObjectsEx
  libcef.dll!0x34FDAAD
  libcef.dll!0x34FD694
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ```

- 852 switches (14.1 %)

  ```text
  win32u.dll!ZwUserGetQueueStatus
  libcef.dll!0x13D2AD2
  libcef.dll!0x34FD6B3
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ```

- 474 switches (7.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18C77AF
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  ```

- 229 switches (3.8 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 165 switches (2.7 %) `(kernel only)`
- 162 switches (2.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C4B2
  libcef.dll!0x3637B5C
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x128C06C
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 150 switches (2.5 %)

  ```text
  ntdll.dll!NtGetContextThread
  KernelBase.dll!GetThreadContext
  libcef.dll!0x82016F0
  libcef.dll!0x820196B
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  ```

- 111 switches (1.8 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  libcef.dll!0x34FE256
  libcef.dll!0x34FDD9D
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0xE4A82B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

- 95 switches (1.6 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  libcef.dll!0x34FE256
  libcef.dll!0x34FDD9D
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x183466B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

- 68 switches (1.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

- 52 switches (0.9 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 51 switches (0.8 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x25D45CD
  libcef.dll!0x39AB20C
  libcef.dll!0x25D44D8
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 50 switches (0.8 %)

  ```text
  ntdll.dll!NtResumeThread
  KernelBase.dll!ResumeThread
  libcef.dll!0x82013A7
  libcef.dll!0x8201840
  libcef.dll!0x82019E3
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

- 43 switches (0.7 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  libcef.dll!0x34FE256
  libcef.dll!0x34FDD9D
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 43 switches (0.7 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  ```

- 28 switches (0.5 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  ```

- 19 switches (0.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  steamclient64.dll!0x8A132A
  steamwebhelper.exe!0x14F9D6
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 18 switches (0.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x13EC3DE
  libcef.dll!0x13EC5B9
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 16 switches (0.3 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2D2B5E4
  libcef.dll!0x2BB8A94
  libcef.dll!0x2BB8E44
  ```

- 11 switches (0.2 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  ```

### Thread 8056 CrRendererMain: 492 ms, 722 switches

### Thread 8056 hottest stacks

- 175 ms (35.6 %) `(no stack)`
- 19 ms (3.9 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2761A4
  ```

- 18 ms (3.7 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2761A4
  libcef.dll!0x2B0087B
  ```

- 15 ms (3.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18C77AF
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  libcef.dll!0x338734
  libcef.dll!0x31DB09
  ```

### Thread 8056 wait sites

- 472 switches (65.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18C77AF
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  ```

- 16 switches (2.2 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  libcef.dll!0x2D2B5E4
  libcef.dll!0x2BB8A94
  libcef.dll!0x2BB8E44
  ```

- 9 switches (1.2 %)

  ```text
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  ?!0x0
  libcef.dll!0x32F27DC
  libcef.dll!0x32F2333
  libcef.dll!0x876E9C
  libcef.dll!0x2E39E78
  libcef.dll!0x7DADB0
  libcef.dll!0x3710C04
  ```

- 4 switches (0.6 %)

  ```text
  libcef.dll!0x28A3B35
  libcef.dll!0x38F8453
  libcef.dll!0x3737743
  libcef.dll!0x37370F5
  libcef.dll!0x1D807EE
  libcef.dll!0x69785EE
  libcef.dll!0x2D2EBB5
  libcef.dll!0x355D89C
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  ```

### Thread 12692 CrBrowserMain: 343 ms, 3641 switches

### Thread 12692 hottest stacks

- 113 ms (33.0 %) `(no stack)`
- 80 ms (23.3 %)

  ```text
  win32u.dll!ZwUserMsgWaitForMultipleObjectsEx
  libcef.dll!0x34FDAAD
  libcef.dll!0x34FD694
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 18 ms (5.2 %)

  ```text
  win32u.dll!ZwUserGetQueueStatus
  libcef.dll!0x13D2AD2
  libcef.dll!0x34FD6B3
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 16 ms (4.7 %) `(kernel only)`

### Thread 12692 wait sites

- 2691 switches (73.9 %)

  ```text
  win32u.dll!ZwUserMsgWaitForMultipleObjectsEx
  libcef.dll!0x34FDAAD
  libcef.dll!0x34FD694
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ```

- 852 switches (23.4 %)

  ```text
  win32u.dll!ZwUserGetQueueStatus
  libcef.dll!0x13D2AD2
  libcef.dll!0x34FD6B3
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ```

- 4 switches (0.1 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FD623
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  ```

- 4 switches (0.1 %)

  ```text
  win32u.dll!ZwUserGetQueueStatus
  libcef.dll!0x34FD9D3
  libcef.dll!0x34FD694
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x3383E6
  steamwebhelper.exe!0x16C7CF
  steamwebhelper.exe!0x1F1342
  steamwebhelper.exe!0x1F2FB1
  steamwebhelper.exe!0x33B0EA
  kernel32.dll!BaseThreadInitThunk
  ```

### Thread 8180 StackSamplingProfiler: 39 ms, 600 switches

### Thread 8180 hottest stacks

- 15 ms (38.5 %) `(no stack)`
- 4 ms (10.3 %)

  ```text
  ntdll.dll!NtResumeThread
  KernelBase.dll!ResumeThread
  libcef.dll!0x82013A7
  libcef.dll!0x8201840
  libcef.dll!0x82019E3
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  ```

- 4 ms (10.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 ms (7.7 %)

  ```text
  ntdll.dll!NtGetContextThread
  KernelBase.dll!GetThreadContext
  libcef.dll!0x82016F0
  libcef.dll!0x820196B
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  ```

### Thread 8180 wait sites

- 226 switches (37.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 150 switches (25.0 %)

  ```text
  ntdll.dll!NtGetContextThread
  KernelBase.dll!GetThreadContext
  libcef.dll!0x82016F0
  libcef.dll!0x820196B
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  ```

- 50 switches (8.3 %)

  ```text
  ntdll.dll!NtResumeThread
  KernelBase.dll!ResumeThread
  libcef.dll!0x82013A7
  libcef.dll!0x8201840
  libcef.dll!0x82019E3
  libcef.dll!0x74F1CDD
  libcef.dll!0x690C9DB
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

- 9 switches (1.5 %) `?!0x0`

### Thread 9920 network.CrUtilityMain: 12 ms, 60 switches

### Thread 9920 hottest stacks

- 6 ms (50.0 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  ```

- 6 ms (50.0 %) `(no stack)`

### Thread 9920 wait sites

- 37 switches (61.7 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  ```

- 14 switches (23.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  ```

- 3 switches (5.0 %)

  ```text
  libcef.dll!0x1420751
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x1231FB9
  libcef.dll!0x1357603
  ```

- 2 switches (3.3 %)

  ```text
  libcef.dll!0x352372A
  libcef.dll!0x3516EC1
  libcef.dll!0x36344E1
  libcef.dll!0x36338BC
  libcef.dll!0x362AE0A
  libcef.dll!0x362B571
  libcef.dll!0x36330A1
  libcef.dll!0x3506ED2
  libcef.dll!0x362EDF1
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

### Thread 12128 CrGpuMain: 12 ms, 14 switches

### Thread 12128 hottest stacks

- 9 ms (75.0 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  ```

- 2 ms (16.7 %) `(no stack)`
- 1 ms (8.3 %)

  ```text
  ntdll.dll!NtQueryVirtualMemory
  KernelBase.dll!DiscardVirtualMemory
  libcef.dll!0x1901091
  libcef.dll!0x14209A3
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  ```

### Thread 12128 wait sites

- 11 switches (78.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  ```

- 2 switches (14.3 %)

  ```text
  libcef.dll!0x36304C7
  libcef.dll!0x362EDAE
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  libcef.dll!0x1356D11
  libcef.dll!0x1356E0D
  libcef.dll!0x338734
  libcef.dll!0x31DB09
  ```

- 1 switches (7.1 %)

  ```text
  libcef.dll!0x142076B
  libcef.dll!0x141FA7D
  libcef.dll!0x352D11C
  libcef.dll!0x1420FBF
  libcef.dll!0x13DAFCF
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  ```

### Thread 7576 ThreadPoolForegroundWorker: 10 ms, 58 switches

### Thread 7576 hottest stacks

- 4 ms (40.0 %) `(no stack)`
- 1 ms (10.1 %)

  ```text
  ntdll.dll!NtAlpcDeleteSecurityContext
  rpcrt4.dll!LRPC_BINDING_HANDLE::Unbind
  rpcrt4.dll!LRPC_BASE_BINDING_HANDLE::FreeObject
  rpcrt4.dll!REFERENCED_OBJECT::RemoveReference
  rpcrt4.dll!RpcBindingFree
  dhcpcsvc.dll!STRING_HANDLE_unbind
  rpcrt4.dll!Ndr64pClientFinally
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  dhcpcsvc.dll!DhcpEnumInterfaces
  IPHLPAPI.DLL!AddDhcpConfiguration
  IPHLPAPI.DLL!AllocateAndGetAdaptersAddresses
  IPHLPAPI.DLL!GetAdaptersAddresses
  libcef.dll!0x500C30
  ```

- 1 ms (10.0 %)

  ```text
  libcef.dll!0x37C8CD1
  libcef.dll!0x13EDDE0
  libcef.dll!0x3502B3A
  libcef.dll!0x13E8A02
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  libcef.dll!0x39AB330
  libcef.dll!0x25D44D8
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (10.0 %)

  ```text
  libcef.dll!0x19076B0
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  libcef.dll!0x39AB330
  libcef.dll!0x25D44D8
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 7576 wait sites

- 33 switches (56.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x25D45CD
  libcef.dll!0x39AB20C
  libcef.dll!0x25D44D8
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (3.4 %)

  ```text
  ntdll.dll!NtAssociateWaitCompletionPacket
  ntdll.dll!TpSetWaitEx
  ntdll.dll!RtlRegisterWait
  kernel32.dll!RegisterWaitForSingleObject
  libcef.dll!0x34FBE54
  libcef.dll!0x13C70CE
  libcef.dll!0x13C7B77
  libcef.dll!0x3D9AC9D
  libcef.dll!0x58527DA
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  ```

- 2 switches (3.4 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_BASE_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  dnsapi.dll!DnsGetAdaptersInfo
  IPHLPAPI.DLL!AddDnsConfiguration
  IPHLPAPI.DLL!AllocateAndGetAdaptersAddresses
  IPHLPAPI.DLL!GetAdaptersAddresses
  libcef.dll!0x500C30
  libcef.dll!0x4F860C
  libcef.dll!0x4F557C
  ```

- 2 switches (3.4 %)

  ```text
  win32u.dll!ZwUserPostMessage
  user32.dll!PostMessageW
  libcef.dll!0x34FD534
  libcef.dll!0x362E701
  libcef.dll!0x36282FE
  libcef.dll!0x3505D6B
  libcef.dll!0x6910EF8
  libcef.dll!0x1400BFB
  libcef.dll!0x2DDA1F1
  libcef.dll!0x190F86C
  libcef.dll!0x13E8A30
  libcef.dll!0x1907642
  ```

### Thread 9876 Chrome_IOThread: 9 ms, 114 switches

### Thread 9876 hottest stacks

- 3 ms (33.5 %) `(no stack)`
- 2 ms (21.6 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  libcef.dll!0x2D4EC07
  libcef.dll!0x34FE3E2
  libcef.dll!0x34FDD9D
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0xE4A82B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (11.3 %)

  ```text
  libcef.dll!0x3630A44
  libcef.dll!0x362EF01
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0xE4A82B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (11.2 %)

  ```text
  libcef.dll!0x34C5E20
  libcef.dll!0x35073CF
  libcef.dll!0x362EDF1
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0xE4A82B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 9876 wait sites

- 111 switches (97.4 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  libcef.dll!0x34FE256
  libcef.dll!0x34FDD9D
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0xE4A82B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

- 2 switches (1.8 %)

  ```text
  ntdll.dll!ZwWriteFile
  KernelBase.dll!WriteFile
  libcef.dll!0x2D4E415
  libcef.dll!0x2D4CD60
  libcef.dll!0x2D4994F
  libcef.dll!0x2D5009C
  libcef.dll!0x2D56CAB
  libcef.dll!0x2D5EF65
  libcef.dll!0x2D4F0EF
  libcef.dll!0x2D4838D
  libcef.dll!0x35575F0
  libcef.dll!0x359A6C3
  ```

- 1 switches (0.9 %)

  ```text
  win32u.dll!ZwUserPostMessage
  user32.dll!PostMessageW
  libcef.dll!0x34FD534
  libcef.dll!0x362E701
  libcef.dll!0x36282FE
  libcef.dll!0x3505D6B
  libcef.dll!0x3599CFC
  libcef.dll!0x363E667
  libcef.dll!0x69785EE
  libcef.dll!0x2D2EBB5
  libcef.dll!0x355D89C
  libcef.dll!0x350A82D
  ```

### Thread 3768 ThreadPoolServiceThread: 7 ms, 86 switches

### Thread 3768 hottest stacks

- 1 ms (14.3 %)

  ```text
  libcef.dll!0x362C890
  libcef.dll!0x3507218
  libcef.dll!0x362EDF1
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %)

  ```text
  libcef.dll!0x3507329
  libcef.dll!0x362EDF1
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (14.3 %)

  ```text
  libcef.dll!0x353383A
  libcef.dll!0x37C90CA
  libcef.dll!0x1EEE8D0
  libcef.dll!0x1EEE696
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

- 1 ms (14.3 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  libcef.dll!0x350C3EF
  libcef.dll!0x25D474E
  libcef.dll!0x1EF31BF
  libcef.dll!0x1EF336D
  libcef.dll!0x1907196
  libcef.dll!0x1907346
  libcef.dll!0x1EEE6E7
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

### Thread 3768 wait sites

- 67 switches (77.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

- 8 switches (9.3 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  libcef.dll!0x350C3EF
  libcef.dll!0x25D474E
  libcef.dll!0x1EF31BF
  libcef.dll!0x1EF336D
  libcef.dll!0x1907196
  libcef.dll!0x1907346
  libcef.dll!0x1EEE6E7
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  ```

- 2 switches (2.3 %)

  ```text
  libcef.dll!0x2E4CFB0
  libcef.dll!0x362A06C
  libcef.dll!0x3627C0F
  libcef.dll!0x3628847
  libcef.dll!0x1EEE94A
  libcef.dll!0x1EEE696
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  ```

- 1 switches (1.2 %)

  ```text
  libcef.dll!0x350A52D
  libcef.dll!0x1EEE8D0
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ```

### Thread 2364 Compositor: 3 ms, 331 switches

### Thread 2364 hottest stacks

- 1 ms (33.3 %) `(no stack)`
- 1 ms (33.3 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C4B2
  libcef.dll!0x3637B5C
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x128C06C
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (33.3 %) `(kernel only)`

### Thread 2364 wait sites

- 163 switches (49.2 %) `(kernel only)`
- 162 switches (48.9 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C4B2
  libcef.dll!0x3637B5C
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x128C06C
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 4 switches (1.2 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  libcef.dll!0x34FCD82
  libcef.dll!0x350C618
  libcef.dll!0x3637B29
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x128C06C
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (0.6 %)

  ```text
  libcef.dll!0x3D93211
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x128C06C
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

## RTSS.exe

### Threads

|  PID |  TID | Name | CPU ms | Switches | Switches/s |
| ---: | ---: | ---- | -----: | -------: | ---------: |
| 5988 | 5992 |      |     34 |      291 |          5 |
| 5988 | 9792 |      |      1 |       57 |          1 |
| 5988 | 3556 |      |      0 |        2 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 35 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 13 ms (36.9 %) `?`
- 2 ms (5.7 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 1 ms (3.1 %) `mfc140.dll!CWnd::GetOwner`
- 1 ms (3.0 %) `ntoskrnl.exe!EtwpEventWriteFull`
- 1 ms (3.0 %) `ntoskrnl.exe!ObpLookupObjectName`
- 1 ms (2.9 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 1 ms (2.8 %)
  `ucrtbase.dll!__crt_stdio_output::output_processor<char,__crt_stdio_output::string_output_adapter<char>,__crt_stdio_output::standard_base<char,__crt_stdio_output::string_output_adapter<char> > >::type_case_integer<10>`
- 1 ms (2.8 %) `mfc140.dll!CHandleMap::FromHandle`
- 1 ms (2.8 %) `ntoskrnl.exe!RtlpGetEntireXStateAreaLength2`
- 1 ms (2.8 %) `user32.dll!SendMessageWorker`
- 1 ms (2.8 %) `ntoskrnl.exe!KiStartRescheduleContext`
- 1 ms (2.8 %) `ntoskrnl.exe!KiAbEntryRemoveFromTree`
- 1 ms (2.8 %) `ntoskrnl.exe!MiReturnPageTablePageCommitment`
- 1 ms (2.8 %) `ntoskrnl.exe!EtwpLogKernelEvent`
- 1 ms (2.8 %) `ntoskrnl.exe!ExpInterlockedPopEntrySListEnd`
- 1 ms (2.8 %) `user32.dll!ValidateHwnd`
- 1 ms (2.8 %) `win32u.dll!Wow64SystemServiceCall`
- 1 ms (2.8 %) `win32kbase.sys!McTemplateK0cq_EtwWriteTransfer`
- 1 ms (2.8 %) `user32.dll!RtlWCSMessageWParamCharToMB`
- 1 ms (2.8 %) `win32kbase.sys!EtwTraceEndAppMessageProcessing`

### Hottest sampled stacks (leaf first)

- 13 ms (36.9 %) `(no stack)`
- 5 ms (14.4 %)

  ```text
  wow64win.dll!NtUserGetMessage
  wow64win.dll!whNtUserGetMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageA
  mfc140.dll!AfxInternalPumpMessage
  ```

- 4 ms (11.4 %)

  ```text
  wow64win.dll!ZwUserPeekMessage
  wow64win.dll!whNtUserPeekMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageA
  ```

- 2 ms (5.7 %)

  ```text
  ntdll.dll!ZwUnmapViewOfSectionEx
  wow64.dll!whNtUnmapViewOfSectionEx
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!NtUnmapViewOfSectionEx
  KernelBase.dll!UnmapViewOfFile
  OverlayEditor.dll!0x3BAB6
  ```

- 1 ms (3.1 %)

  ```text
  mfc140.dll!CWnd::GetOwner
  mfc140.dll!CWnd::GetTopLevelParent
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (3.0 %)

  ```text
  ntdll.dll!ZwOpenSection
  wow64.dll!whNtOpenSection
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!ZwOpenSection
  KernelBase.dll!OpenFileMappingW
  kernel32.dll!OpenFileMappingA
  ```

- 1 ms (2.8 %)

  ```text
  ucrtbase.dll!__crt_stdio_output::output_processor<char,__crt_stdio_output::string_output_adapter<char>,__crt_stdio_output::standard_base<char,__crt_stdio_output::string_output_adapter<char> > >::type_case_integer<10>
  ucrtbase.dll!__crt_stdio_output::output_processor<char,__crt_stdio_output::string_output_adapter<char>,__crt_stdio_output::standard_base<char,__crt_stdio_output::string_output_adapter<char> > >::process
  ucrtbase.dll!___stdio_common_vsprintf
  mfc140.dll!ATL::ChTraitsCRT<char>::GetFormattedLength
  mfc140.dll!ATL::CStringT<char,StrTraitMFC_DLL<char,ATL::ChTraitsCRT<char> > >::Format
  OverlayEditor.dll!0x54AA8
  OverlayEditor.dll!0x54B92
  OverlayEditor.dll!0x36BC1
  OverlayEditor.dll!0x4B867
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  ```

- 1 ms (2.8 %)

  ```text
  mfc140.dll!CHandleMap::FromHandle
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (2.8 %)

  ```text
  user32.dll!SendMessageWorker
  user32.dll!SendMessageA
  RTMUI.dll!0x890F
  RTSS.exe!0x1A6EA
  mfc140.dll!CWnd::WalkPreTranslateTree
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (2.8 %) `(kernel only)`
- 1 ms (2.8 %)

  ```text
  user32.dll!ValidateHwnd
  user32.dll!SendMessageA
  RTMUI.dll!0x890F
  RTSS.exe!0x1A6EA
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (2.8 %)

  ```text
  win32u.dll!Wow64SystemServiceCall
  user32.dll!PeekMessageA
  OverlayEditor.dll!0x30CD8
  OverlayEditor.dll!0x4B872
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  user32.dll!DispatchMessageA
  mfc140.dll!AfxInternalPumpMessage
  ```

- 1 ms (2.8 %)

  ```text
  user32.dll!RtlWCSMessageWParamCharToMB
  user32.dll!PeekMessageA
  mfc140.dll!CWinThread::Run
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (2.8 %)

  ```text
  user32.dll!_HANDLEENTRY_FROM_INDEX
  user32.dll!HMValidateHandleNoRip
  user32.dll!IsWindow
  RTMUI.dll!0x887F
  RTSS.exe!0x1A6EA
  mfc140.dll!CWnd::WalkPreTranslateTree
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ```

- 1 ms (2.8 %)

  ```text
  ntdll.dll!RtlEnterCriticalSection
  mfc140.dll!CThreadSlotData::GetThreadValue
  mfc140.dll!CThreadLocalObject::GetData
  mfc140.dll!AfxGetModuleThreadState
  mfc140.dll!CWnd::FromHandlePermanent
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!SendMessageWorker
  user32.dll!SendMessageInternal
  user32.dll!SendMessageA
  RTMUI.dll!0x890F
  RTSS.exe!0x1A6EA
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 176 switches (50.3 %)

  ```text
  wow64win.dll!NtUserGetMessage
  wow64win.dll!whNtUserGetMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserGetMessage
  ```

- 37 switches (10.6 %)

  ```text
  wow64win.dll!ZwUserPeekMessage
  wow64win.dll!whNtUserPeekMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserPeekMessage
  ```

- 32 switches (9.1 %)

  ```text
  wow64cpu.dll!CpupSyscallStub
  wow64cpu.dll!WaitForMultipleObjects32
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!ZwWaitForMultipleObjects
  winmm.dll!timeThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ```

- 25 switches (7.1 %)

  ```text
  wow64win.dll!ZwUserPostMessage
  wow64win.dll!whNT32NtUserPostMessageCB
  wow64win.dll!whNtUserPostMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserPostMessage
  ```

- 19 switches (5.4 %)

  ```text
  wow64win.dll!ZwUserFindWindowEx
  wow64win.dll!whNtUserFindWindowEx
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserFindWindowEx
  ```

- 3 switches (0.9 %)

  ```text
  OverlayEditor.dll!0x3B2CE
  OverlayEditor.dll!0x4A3F1
  OverlayEditor.dll!0x36BAA
  OverlayEditor.dll!0x4B867
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  ```

- 2 switches (0.6 %)

  ```text
  mfc140.dll!CThreadLocalObject::GetData
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (0.6 %)

  ```text
  kernel32.dll!GetTickCountStub
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  user32.dll!DispatchMessageA
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  ```

- 2 switches (0.6 %)

  ```text
  mfc140.dll!CThreadSlotData::GetThreadValue
  mfc140.dll!CThreadLocal<_AFX_THREAD_STATE>::GetData
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (0.6 %)

  ```text
  ntdll.dll!ZwUnmapViewOfSectionEx
  wow64.dll!whNtUnmapViewOfSectionEx
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!NtUnmapViewOfSectionEx
  ```

- 2 switches (0.6 %)

  ```text
  KernelBase.dll!GetTickCount
  kernel32.dll!GetTickCountStub
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  user32.dll!DispatchMessageA
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  ```

- 2 switches (0.6 %)

  ```text
  user32.dll!IsWindow
  RTSS.exe!0x1A6EA
  mfc140.dll!CWnd::WalkPreTranslateTree
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (0.6 %)

  ```text
  ntdll.dll!LdrGetProcedureAddressForCaller
  KernelBase.dll!GetProcAddressForCaller
  kernel32.dll!GetProcAddressStub
  RTFC.dll!0x7BEB
  RTSS.exe!0x174F5
  RTSS.exe!0x1A500
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  ```

- 2 switches (0.6 %)

  ```text
  mfc140.dll!CPlex::FreeDataChain
  mfc140.dll!AfxUnlockTempMaps
  mfc140.dll!CWinThread::OnIdle
  mfc140.dll!CWinApp::OnIdle
  mfc140.dll!CWinThread::Run
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (0.6 %)

  ```text
  mfc140.dll!CMapPtrToPtr::GetValueAt
  mfc140.dll!CWnd::WalkPreTranslateTree
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 switches (0.3 %)

  ```text
  user32.dll!HMValidateHandleWithDescriptor
  user32.dll!GetWindowLongW
  comctl32.dll!CToolTipsMgr::s_ToolTipsWndProc
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!CallWindowProcAorW
  user32.dll!CallWindowProcA
  mfc140.dll!CWnd::DefWindowProcA
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  ```

- 1 switches (0.3 %)

  ```text
  ntdll.dll!ZwOpenSection
  wow64.dll!whNtOpenSection
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!ZwOpenSection
  ```

- 1 switches (0.3 %)

  ```text
  ntdll.dll!RtlCustomCPToUnicodeN
  ntdll.dll!RtlMultiByteToUnicodeN
  user32.dll!RtlCaptureAnsiString
  user32.dll!InternalFindWindowExA
  RTSS.exe!0x16C9D
  RTSS.exe!0x1A500
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  ```

- 1 switches (0.3 %)

  ```text
  ntdll.dll!RtlActivateActivationContextUnsafeFast
  user32.dll!UserCallWinProcCheckWow
  user32.dll!SendMessageWorker
  user32.dll!SendMessageInternal
  user32.dll!SendMessageA
  RTMUI.dll!0x890F
  RTSS.exe!0x1A6EA
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  ```

- 1 switches (0.3 %)

  ```text
  RTMUI.dll!0x888D
  RTSS.exe!0x1A6EA
  mfc140.dll!CWnd::WalkPreTranslateTree
  mfc140.dll!AfxInternalPreTranslateMessage
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxPreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

### Thread 5992 : 34 ms, 291 switches

### Thread 5992 hottest stacks

- 12 ms (35.1 %) `(no stack)`
- 5 ms (14.8 %)

  ```text
  wow64win.dll!NtUserGetMessage
  wow64win.dll!whNtUserGetMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageA
  mfc140.dll!AfxInternalPumpMessage
  ```

- 4 ms (11.7 %)

  ```text
  wow64win.dll!ZwUserPeekMessage
  wow64win.dll!whNtUserPeekMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageA
  ```

- 2 ms (5.8 %)

  ```text
  ntdll.dll!ZwUnmapViewOfSectionEx
  wow64.dll!whNtUnmapViewOfSectionEx
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!NtUnmapViewOfSectionEx
  KernelBase.dll!UnmapViewOfFile
  OverlayEditor.dll!0x3BAB6
  ```

### Thread 5992 wait sites

- 176 switches (60.5 %)

  ```text
  wow64win.dll!NtUserGetMessage
  wow64win.dll!whNtUserGetMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserGetMessage
  ```

- 37 switches (12.7 %)

  ```text
  wow64win.dll!ZwUserPeekMessage
  wow64win.dll!whNtUserPeekMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserPeekMessage
  ```

- 19 switches (6.5 %)

  ```text
  wow64win.dll!ZwUserFindWindowEx
  wow64win.dll!whNtUserFindWindowEx
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserFindWindowEx
  ```

- 3 switches (1.0 %)

  ```text
  OverlayEditor.dll!0x3B2CE
  OverlayEditor.dll!0x4A3F1
  OverlayEditor.dll!0x36BAA
  OverlayEditor.dll!0x4B867
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  ```

### Thread 9792 : 1 ms, 57 switches

### Thread 9792 hottest stacks

- 1 ms (100.0 %) `(no stack)`

### Thread 9792 wait sites

- 32 switches (56.1 %)

  ```text
  wow64cpu.dll!CpupSyscallStub
  wow64cpu.dll!WaitForMultipleObjects32
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!ZwWaitForMultipleObjects
  winmm.dll!timeThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ```

- 25 switches (43.9 %)

  ```text
  wow64win.dll!ZwUserPostMessage
  wow64win.dll!whNT32NtUserPostMessageCB
  wow64win.dll!whNtUserPostMessage
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  win32u.dll!NtUserPostMessage
  ```

### Thread 3556 : 0 ms, 2 switches

### Thread 3556 wait sites

- 1 switches (50.0 %) `ntdll.dll!RtlUserThreadStart`
- 1 switches (50.0 %)

  ```text
  ntdll.dll!_memcpy
  ntdll.dll!LdrpAllocateTls
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

## RTSSHooksLoader64.exe

### Threads

|  PID |   TID | Name | CPU ms | Switches | Switches/s |
| ---: | ----: | ---- | -----: | -------: | ---------: |
| 1308 | 14188 |      |      2 |        3 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 2 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 1 ms (51.1 %) `ntoskrnl.exe!MiUserFault`
- 1 ms (48.9 %) `ntdll.dll!RtlpAllocateHeap`

### Hottest sampled stacks (leaf first)

- 1 ms (51.1 %)

  ```text
  ntdll.dll!RtlpAllocateHeap
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  ucrtbase.dll!_calloc_base
  ucrtbase.dll!internal_get_ptd_head_slow
  ucrtbase.dll!__acrt_getptd_noexit
  ucrtbase.dll!DllMainDispatch
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 1 ms (48.9 %)

  ```text
  ntdll.dll!RtlpAllocateHeap
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  msvcrt.dll!_calloc_impl
  msvcrt.dll!_calloc_crt
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 2 switches (66.7 %) `ntdll.dll!RtlUserThreadStart`
- 1 switches (33.3 %)

  ```text
  advapi32.dll!_DllInitialize
  advapi32.dll!DllInitialize
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

### Thread 14188 : 2 ms, 3 switches

### Thread 14188 hottest stacks

- 1 ms (51.1 %)

  ```text
  ntdll.dll!RtlpAllocateHeap
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  ucrtbase.dll!_calloc_base
  ucrtbase.dll!internal_get_ptd_head_slow
  ucrtbase.dll!__acrt_getptd_noexit
  ucrtbase.dll!DllMainDispatch
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 1 ms (48.9 %)

  ```text
  ntdll.dll!RtlpAllocateHeap
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  msvcrt.dll!_calloc_impl
  msvcrt.dll!_calloc_crt
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

### Thread 14188 wait sites

- 2 switches (66.7 %) `ntdll.dll!RtlUserThreadStart`
- 1 switches (33.3 %)

  ```text
  advapi32.dll!_DllInitialize
  advapi32.dll!DllInitialize
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

## LHMDataProvider.exe

### Threads

|  PID |   TID | Name | CPU ms | Switches | Switches/s |
| ---: | ----: | ---- | -----: | -------: | ---------: |
| 9776 | 13048 |      |    132 |     1348 |         25 |
| 9776 | 12920 |      |     14 |       13 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 146 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 65 ms (44.5 %) `?`
- 8 ms (5.5 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 2 ms (1.4 %) `ntoskrnl.exe!ExAllocateHeapPool`
- 2 ms (1.4 %) `ntdll.dll!RtlpxVirtualUnwind`
- 2 ms (1.4 %) `clr.dll!StubHelpers::DemandPermission`
- 2 ms (1.4 %) `ntoskrnl.exe!NtSetInformationThread`
- 2 ms (1.4 %) `vcruntime140_clr0400.dll!memcpy_repmovs`
- 2 ms (1.4 %) `vcruntime140_clr0400.dll!MoveSmall4`
- 2 ms (1.4 %) `vcruntime140_clr0400.dll!memset_repmovs`
- 1 ms (0.7 %) `ntoskrnl.exe!ExpLookupHandleTableEntry`
- 1 ms (0.7 %) `clr.dll!FieldMarshaler::NativeSize`
- 1 ms (0.7 %) `ntoskrnl.exe!MiUnlockPageTableInternal`
- 1 ms (0.7 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 1 ms (0.7 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 1 ms (0.7 %) `IshOed.sys!0x768E`
- 1 ms (0.7 %) `LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0`
- 1 ms (0.7 %) `mscorlib.ni.dll!0x5AF6E0`
- 1 ms (0.7 %) `?!0x0`
- 1 ms (0.7 %) `ntdll.dll!RtlpLowFragHeapAllocFromContext`
- 1 ms (0.7 %) `combase.dll!CoTaskMemAlloc`

### Hottest sampled stacks (leaf first)

- 65 ms (44.5 %) `(no stack)`
- 11 ms (7.5 %)

  ```text
  ntdll.dll!ZwSetInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  ```

- 6 ms (4.1 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.KernelDriver::DeviceIOControl 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 6 ms (4.1 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  clr.dll!EESleepEx
  clr.dll!Thread::UserSleep
  clr.dll!ThreadNative::Sleep
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 5 ms (3.4 %)

  ```text
  win32u.dll!NtGdiDdDDIOpenAdapterFromDeviceName
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.D3DDisplayDevice::GetDeviceInfoByIdentifier 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Gpu.IntelIntegratedGpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  ```

- 3 ms (2.1 %) `(kernel only)`
- 3 ms (2.1 %)

  ```text
  ntdll.dll!NtQueryInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  ```

- 2 ms (1.4 %)

  ```text
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  ```

- 2 ms (1.4 %)

  ```text
  vcruntime140_clr0400.dll!memcpy_repmovs
  mscorlib.ni.dll!0x55CC44
  mscorlib.ni.dll!0x56D334
  mscorlib.ni.dll!0x52BC06
  mscorlib.ni.dll!0x52BB7C
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  ```

- 2 ms (1.4 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWrite
  clr.dll!CoTemplate_xxth
  clr.dll!ETW::GCLog::GCSettingsEvent
  clr.dll!EtwCallback
  clr.dll!McGenControlCallbackV2
  ntdll.dll!EtwpEventApiCallback
  ntdll.dll!EtwpUpdateEnableInfoAndCallback
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 ms (1.4 %)

  ```text
  vcruntime140_clr0400.dll!MoveSmall4
  mscorlib.ni.dll!0x55CC44
  mscorlib.ni.dll!0x56D334
  mscorlib.ni.dll!0x52BC06
  mscorlib.ni.dll!0x52BB7C
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  ```

- 2 ms (1.4 %)

  ```text
  vcruntime140_clr0400.dll!memset_repmovs
  clr.dll!WKS::gc_heap::adjust_limit_clr
  clr.dll!WKS::gc_heap::a_fit_segment_end_p
  clr.dll!WKS::gc_heap::soh_try_fit
  clr.dll!WKS::gc_heap::allocate_small
  clr.dll!WKS::gc_heap::try_allocate_more_space
  clr.dll!WKS::GCHeap::Alloc
  clr.dll!FramedAllocateString
  mscorlib.ni.dll!0x52BB6B
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 1 ms (0.7 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.KernelDriver::DeviceIOControl 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  ```

- 1 ms (0.7 %)

  ```text
  ntdll.dll!RtlpxVirtualUnwind
  ntdll.dll!RtlUnwindEx
  ntdll.dll!__C_specific_handler
  KernelBase.dll!__GSHandlerCheck_SEH
  ntdll.dll!RtlpExecuteHandlerForException
  ntdll.dll!RtlDispatchException
  ntdll.dll!RtlRaiseException
  KernelBase.dll!RaiseException
  KernelBase.dll!OutputDebugStringA
  LHMDataProvider.exe!0x12AF
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  ```

- 1 ms (0.7 %)

  ```text
  clr.dll!FieldMarshaler::NativeSize
  clr.dll!FieldMarshaler_FixedArray::UpdateCLRImpl
  clr.dll!FieldMarshaler::UpdateCLR
  clr.dll!LayoutUpdateCLR
  clr.dll!FmtValueTypeUpdateCLR
  clr.dll!StubHelpers::ValueClassMarshaler__ConvertToManaged
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  ```

- 1 ms (0.7 %)

  ```text
  ntdll.dll!ZwCreateMutant
  KernelBase.dll!CreateMutexExW
  KernelBase.dll!CreateMutexA
  LHMDataProvider.exe!0x1227
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!CWinThread::Run
  ```

- 1 ms (0.7 %)

  ```text
  RTSSHooks64.dll!0x12080B
  RTSSHooks64.dll!0xC535D
  RTSSHooks64.dll!0xC49C1
  RTSSHooks64.dll!0xC1A49
  RTSSHooks64.dll!0xC237D
  RTSSHooks64.dll!0xC25AB
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 1 ms (0.7 %)

  ```text
  mscorlib.ni.dll!0x5AF6E0
  mscorlib.ni.dll!0x53351E
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.KernelDriver::DeviceIOControl 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  ```

- 1 ms (0.7 %)

  ```text
  ?!0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  ```

- 1 ms (0.7 %)

  ```text
  ntdll.dll!RtlpLowFragHeapAllocFromContext
  ntdll.dll!RtlpAllocateNTHeapInternal
  ntdll.dll!RtlAllocateHeap
  mscorlib.ni.dll!0x568165
  mscorlib.ni.dll!0x5AF712
  mscorlib.ni.dll!0x53351E
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.KernelDriver::DeviceIOControl 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 715 switches (52.5 %)

  ```text
  ntdll.dll!ZwSetInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 315 switches (23.1 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  clr.dll!EESleepEx
  clr.dll!Thread::UserSleep
  clr.dll!ThreadNative::Sleep
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  ```

- 134 switches (9.8 %)

  ```text
  ntdll.dll!ZwQuerySystemInformation
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.CpuLoad::GetWindowsTimes 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.CpuLoad::Update 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.GenericCpu::Update 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  ```

- 53 switches (3.9 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageA
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!CWinThread::Run
  mfc140.dll!AfxWinMain
  LHMDataProvider.exe!0x222E
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 31 switches (2.3 %)

  ```text
  ntdll.dll!ZwSetInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  ```

- 26 switches (1.9 %)

  ```text
  ntdll.dll!ZwSetInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.GenericCpu::Update 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  ```

- 4 switches (0.3 %)

  ```text
  ntdll.dll!RtlEnterCriticalSection
  msvcrt.dll!_initptd
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 4 switches (0.3 %)

  ```text
  win32u.dll!NtGdiDdDDIOpenAdapterFromDeviceName
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.D3DDisplayDevice::GetDeviceInfoByIdentifier 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Gpu.IntelIntegratedGpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  ```

- 3 switches (0.2 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (0.1 %)

  ```text
  RTSSHooks64.dll!0xC52CF
  RTSSHooks64.dll!0xC49C1
  RTSSHooks64.dll!0xC1A49
  RTSSHooks64.dll!0xC237D
  RTSSHooks64.dll!0xC25AB
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (0.1 %)

  ```text
  RTSSHooks64.dll!0x12080B
  RTSSHooks64.dll!0xC535D
  RTSSHooks64.dll!0xC49C1
  RTSSHooks64.dll!0xC1A49
  RTSSHooks64.dll!0xC237D
  RTSSHooks64.dll!0xC25AB
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (0.1 %)

  ```text
  ntdll.dll!ZwQuerySystemInformation
  KernelBase.dll!GlobalMemoryStatusEx
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Memory.GenericWindowsMemory::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  ```

- 2 switches (0.1 %)

  ```text
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Sensor::set_Value 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  ```

- 2 switches (0.1 %)

  ```text
  clr.dll!ArrayNative::ArrayCopy
  mscorlib.ni.dll!0x544BA5
  mscorlib.ni.dll!0x53D8DD
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::get_Hardware 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  ```

- 2 switches (0.1 %)

  ```text
  ntdll.dll!RtlpTimeToTimeFields
  KernelBase.dll!FileTimeToSystemTime
  clr.dll!SystemNative::GetSystemTimeWithLeapSecondsHandling
  mscorlib.ni.dll!0x5854FE
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Sensor::set_Value 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  ```

- 2 switches (0.1 %) `?!0x0`
- 2 switches (0.1 %)

  ```text
  mscorlib.ni.dll!0x57F590
  System.Core.ni.dll!0x345893
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Hardware::ActivateSensor 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  ```

- 2 switches (0.1 %)

  ```text
  mfc140.dll!AfxLockGlobals
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!CWinThread::Run
  mfc140.dll!AfxWinMain
  LHMDataProvider.exe!0x222E
  ```

- 2 switches (0.1 %)

  ```text
  win32u.dll!NtGdiDdDDIQueryStatistics
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.D3DDisplayDevice::GetQueryStatisticsNode 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.D3DDisplayDevice::GetDeviceInfoByIdentifier 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Gpu.IntelIntegratedGpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 2 switches (0.1 %)

  ```text
  ntdll.dll!NtQueryInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

### Thread 13048 : 132 ms, 1348 switches

### Thread 13048 hottest stacks

- 54 ms (40.9 %) `(no stack)`
- 11 ms (8.3 %)

  ```text
  ntdll.dll!ZwSetInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  ```

- 6 ms (4.6 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  KernelBase.dll!DeviceIoControl
  kernel32.dll!DeviceIoControlImplementation
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.KernelDriver::DeviceIOControl 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Ring0::ReadMsr 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 6 ms (4.5 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  clr.dll!EESleepEx
  clr.dll!Thread::UserSleep
  clr.dll!ThreadNative::Sleep
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

### Thread 13048 wait sites

- 715 switches (53.0 %)

  ```text
  ntdll.dll!ZwSetInformationThread
  KernelBase.dll!SetThreadGroupAffinity
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.ThreadAffinity::Set 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  ```

- 315 switches (23.4 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  clr.dll!EESleepEx
  clr.dll!Thread::UserSleep
  clr.dll!ThreadNative::Sleep
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  ```

- 134 switches (9.9 %)

  ```text
  ntdll.dll!ZwQuerySystemInformation
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.CpuLoad::GetWindowsTimes 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.CpuLoad::Update 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.GenericCpu::Update 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.IntelCpu::Update 0x0
  Exporter.dll!Exporter.ExportClass+UpdateVisitor::VisitHardware 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Computer::Traverse 0x0
  Exporter.dll!Exporter.ExportClass::ExportToXML 0x0
  Importer.dll!Importer.DoExport::ExportToXML 0x0
  Importer.dll!<Module>::Importer.DoImport 0x0
  Importer.dll!<Module>::ImportXML 0x0
  ```

- 53 switches (3.9 %)

  ```text
  win32u.dll!NtUserGetMessage
  user32.dll!GetMessageA
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!CWinThread::Run
  mfc140.dll!AfxWinMain
  LHMDataProvider.exe!0x222E
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 12920 : 14 ms, 13 switches

### Thread 12920 hottest stacks

- 11 ms (78.5 %) `(no stack)`
- 2 ms (14.3 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWrite
  clr.dll!CoTemplate_xxth
  clr.dll!ETW::GCLog::GCSettingsEvent
  clr.dll!EtwCallback
  clr.dll!McGenControlCallbackV2
  ntdll.dll!EtwpEventApiCallback
  ntdll.dll!EtwpUpdateEnableInfoAndCallback
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (7.2 %)

  ```text
  RTSSHooks64.dll!0x12080B
  RTSSHooks64.dll!0xC535D
  RTSSHooks64.dll!0xC49C1
  RTSSHooks64.dll!0xC1A49
  RTSSHooks64.dll!0xC237D
  RTSSHooks64.dll!0xC25AB
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

### Thread 12920 wait sites

- 4 switches (30.8 %)

  ```text
  ntdll.dll!RtlEnterCriticalSection
  msvcrt.dll!_initptd
  msvcrt.dll!__CRTDLL_INIT
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 3 switches (23.1 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (15.4 %)

  ```text
  RTSSHooks64.dll!0xC52CF
  RTSSHooks64.dll!0xC49C1
  RTSSHooks64.dll!0xC1A49
  RTSSHooks64.dll!0xC237D
  RTSSHooks64.dll!0xC25AB
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (15.4 %)

  ```text
  RTSSHooks64.dll!0x12080B
  RTSSHooks64.dll!0xC535D
  RTSSHooks64.dll!0xC49C1
  RTSSHooks64.dll!0xC1A49
  RTSSHooks64.dll!0xC237D
  RTSSHooks64.dll!0xC25AB
  ntdll.dll!LdrpCallInitRoutineInternal
  ntdll.dll!LdrpCallInitRoutine
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```
