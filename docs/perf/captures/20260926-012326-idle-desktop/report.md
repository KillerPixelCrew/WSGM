# Trace trace.etl

Duration 55.2 s. Samples and switches are whole-trace totals; rates are per second of trace.

## Processes

| Process                           |   PID | CPU ms | CPU % of one core | Context switches | Switches/s |
| --------------------------------- | ----: | -----: | ----------------: | ---------------: | ---------: |
| WSGM.exe                          |  7772 |   5672 |             10.27 |           101512 |       1838 |
| Idle                              |     0 |   3425 |              6.20 |           171441 |       3104 |
| VBCSCompiler.exe                  |  3948 |   3351 |              6.07 |              154 |          3 |
| System                            |     4 |   1743 |              3.16 |            20342 |        368 |
| WUDFHost.exe                      |  1404 |   1449 |              2.62 |            27408 |        496 |
| pwsh.exe                          |  4748 |   1388 |              2.51 |             2182 |         40 |
| esrv_svc.exe                      |  6472 |   1377 |              2.49 |             1177 |         21 |
| Taskmgr.exe                       | 16432 |   1373 |              2.49 |             3580 |         65 |
| rustdesk.exe                      |  5592 |   1235 |              2.24 |            15239 |        276 |
| IntelGraphicsSoftware.exe         | 11516 |   1009 |              1.83 |             3781 |         68 |
| steam.exe                         | 13984 |    973 |              1.76 |            15863 |        287 |
| rustdesk.exe                      | 14400 |    675 |              1.22 |             8613 |        156 |
| claude.exe                        | 12608 |    610 |              1.10 |             6657 |        121 |
| dotnet-counters.exe               | 14312 |    585 |              1.06 |             3616 |         65 |
| DSAService.exe                    |  4008 |    578 |              1.05 |              741 |         13 |
| WmiPrvSE.exe                      | 15036 |    564 |              1.02 |              701 |         13 |
| pwsh.exe                          | 14860 |    534 |              0.97 |             1238 |         22 |
| svchost.exe                       |  1744 |    437 |              0.79 |             2210 |         40 |
| DSATray.exe                       | 11132 |    344 |              0.62 |              120 |          2 |
| dwm.exe                           |  1600 |    341 |              0.62 |             1837 |         33 |
| svchost.exe                       |  4320 |    311 |              0.56 |             1214 |         22 |
| svchost.exe                       |  3356 |    295 |              0.53 |             3066 |         56 |
| steamwebhelper.exe                |  1612 |    295 |              0.53 |             4599 |         83 |
| IntelGraphicsSoftware.Overlay.exe | 12236 |    278 |              0.50 |              291 |          5 |
| rustdesk.exe                      |  6956 |    250 |              0.45 |             5607 |        102 |
| iGoSwServer.exe                   |  5972 |    236 |              0.43 |             1478 |         27 |
| wpr.exe                           |  6380 |    233 |              0.42 |              105 |          2 |
| PresentMonService.exe             |  6424 |    221 |              0.40 |             1662 |         30 |
| pwsh.exe                          | 17340 |    219 |              0.40 |              356 |          6 |
| WSGM.exe                          | 16396 |    211 |              0.38 |              299 |          5 |
| svchost.exe                       |  1324 |    196 |              0.35 |             1580 |         29 |
| csrss.exe                         |  1044 |    194 |              0.35 |             3293 |         60 |
| WmiPrvSE.exe                      | 12840 |    178 |              0.32 |              992 |         18 |
| IntelGraphicsSoftware.Service.exe |  2792 |    169 |              0.31 |               99 |          2 |
| explorer.exe                      | 10664 |    155 |              0.28 |             1154 |         21 |
| LHMDataProvider.exe               |  9776 |    142 |              0.26 |             1334 |         24 |
| powershell.exe                    | 12696 |    132 |              0.24 |                9 |          0 |
| tail.exe                          | 16880 |    131 |              0.24 |             2961 |         54 |
| wpr.exe                           | 12616 |    109 |              0.20 |              159 |          3 |
| ClawLab-Cursor-Refresh-Helper.exe |  6000 |    106 |              0.19 |             1627 |         29 |

All processes: 32861 ms CPU, 59.5 % of one core.

## WSGM.exe

### Threads

|   PID |   TID | Name                             | CPU ms | Switches | Switches/s |
| ----: | ----: | -------------------------------- | -----: | -------: | ---------: |
|  7772 | 13864 |                                  |    755 |        0 |          0 |
|  7772 | 14060 |                                  |    503 |    35787 |        648 |
|  7772 |  3288 |                                  |    471 |     3083 |         56 |
|  7772 |  5928 | .NET TP Worker                   |    440 |     6216 |        113 |
|  7772 |  6676 |                                  |    429 |     3072 |         56 |
|  7772 | 16484 | .NET TP Worker                   |    377 |     5197 |         94 |
|  7772 |  7016 | .NET TP Worker                   |    345 |     5343 |         97 |
|  7772 |  8124 | .NET Tiered Compilation Worker   |    337 |      111 |          2 |
|  7772 |  9576 | .NET TP Worker                   |    315 |     5545 |        100 |
|  7772 |  9676 |                                  |    297 |     7457 |        135 |
|  7772 |  2796 |                                  |    262 |     5935 |        107 |
|  7772 | 14372 |                                  |    261 |     5217 |         94 |
|  7772 | 16304 |                                  |    248 |     3240 |         59 |
|  7772 | 16676 |                                  |    246 |     5621 |        102 |
| 16396 | 10008 |                                  |    117 |       40 |          1 |
|  7772 | 14516 |                                  |    112 |     3395 |         61 |
|  7772 | 16896 | .NET ThreadPool IO               |    100 |     3821 |         69 |
| 16396 |  2668 | .NET Tiered Compilation Worker   |     86 |      259 |          5 |
|  7772 | 16196 | .NET Timer                       |     31 |     1251 |         23 |
|  7772 |  2144 | MetricsEventSource CollectWorker |     30 |       39 |          1 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 4283 ms (72.8 %) `(no WSGM frame)`
- 907 ms (15.4 %) `libviiper.dll!0x12411`
- 94 ms (1.6 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawControllerSource+<ReadLoopAsync>d__15::MoveNext 0x0`
- 69 ms (1.2 %) `WSGM.dll!WSGM.Core.RtssNativeAdapter::ReadCore 0x0`
- 59 ms (1.0 %) `WSGM.dll!dynamicClass::IL_STUB_PInvoke 0x0`
- 50 ms (0.9 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors+SensorEventSink::OnDataUpdated 0x0`
- 50 ms (0.8 %) `WSGM.dll!dynamicClass::IL_STUB_CLRtoCOM 0x0`
- 35 ms (0.6 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource+<>c__DisplayClass12_0::<OpenSession>b__0 0x0`
- 20 ms (0.3 %) `WSGM.dll!WSGM.Core.WindowFinder::FindProcessIds 0x0`
- 14 ms (0.2 %) `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.MsiWmiPlatform::InvokeCore 0x0`
- 11 ms (0.2 %) `WSGM.dll!WSGM.Core.WindowsRtssDiscoveryEnvironment::ReadProcesses 0x0`
- 9 ms (0.2 %) `WSGM.dll!WSGM.Core.RtssOsdRenderer+<RenderLoopAsync>d__17::MoveNext 0x0`
- 9 ms (0.2 %) `libviiper.dll!0x1C969`
- 7 ms (0.1 %) `WindowsDeviceControl.dll!WindowsDeviceControl.WindowsStorage::DescribeVolumes 0x0`
- 6 ms (0.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::OnReport 0x0`
- 5 ms (0.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.MsiWmiPlatform+<RunSerializedAsync>d__9`1[System.__Canon]::MoveNext
  0x0`
- 5 ms (0.1 %)
  `WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::TryReadNumericValue 0x0`
- 5 ms (0.1 %) `WSGM.dll!WSGM.Shell.ShellSession+<RunSteamUiTransportGateAsync>d__102::MoveNext 0x0`
- 5 ms (0.1 %)
  `SteamUiToolkit.dll!SteamUiToolkit.SteamSurfaceModule+<>c__DisplayClass1_0`1+<<Publication>b__0>d[System.__Canon]::MoveNext
  0x0`
- 5 ms (0.1 %) `WSGM.dll!WSGM.Shell.RemovableDriveManager::ComputeSignature 0x0`

### CPU by leaf function

- 2552 ms (43.4 %) `?`
- 314 ms (5.3 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 217 ms (3.7 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 93 ms (1.6 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 52 ms (0.9 %) `ntoskrnl.exe!RtlpWalkFrameChain`
- 45 ms (0.8 %) `?!0x0`
- 40 ms (0.7 %) `ntoskrnl.exe!SwapContext`
- 33 ms (0.6 %) `ntoskrnl.exe!ExAllocateHeapPool`
- 33 ms (0.6 %) `ntoskrnl.exe!RtlpLookupUserFunctionTableInverted`
- 30 ms (0.5 %) `ntdll.dll!RtlpLowFragHeapAllocFromContext`
- 28 ms (0.5 %) `ntoskrnl.exe!ObpReferenceObjectByHandleWithTag`
- 25 ms (0.4 %) `ntoskrnl.exe!KiPageFault`
- 24 ms (0.4 %) `ntoskrnl.exe!KiSystemServiceUser`
- 22 ms (0.4 %) `ntoskrnl.exe!EtwpLogKernelEvent`
- 21 ms (0.4 %) `ntoskrnl.exe!EtwpReserveTraceBuffer`
- 20 ms (0.3 %) `ntoskrnl.exe!HalpInterruptSendIpi`
- 19 ms (0.3 %) `IshOed.sys!0x768E`
- 18 ms (0.3 %) `ntoskrnl.exe!RtlpGetEntireXStateAreaLength2`
- 17 ms (0.3 %) `ntoskrnl.exe!KiDeferredReadySingleThread`
- 16 ms (0.3 %) `ntoskrnl.exe!KiCommitThreadWait`

### Hottest sampled stacks (leaf first)

- 2552 ms (43.4 %) `(no stack)`
- 277 ms (4.7 %) `(kernel only)`
- 193 ms (3.3 %)

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

- 158 ms (2.7 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 145 ms (2.5 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 137 ms (2.3 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
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

- 129 ms (2.2 %)

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

- 115 ms (2.0 %)

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

- 67 ms (1.1 %)

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

- 42 ms (0.7 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
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
  kernel32.dll!BaseThreadInitThunk
  ```

- 41 ms (0.7 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 36 ms (0.6 %)

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
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::MaybeAddWorkingWorker 0x0
  System.Threading.Channels.dll!System.Threading.Channels.AsyncOperation::SignalCompletion 0x0
  System.Threading.Channels.dll!System.Threading.Channels.ChannelUtilities::DangerousSetOperations 0x0
  System.Threading.Channels.dll!System.Threading.Channels.BoundedChannel`1+BoundedChannelWriter[WSGM.Device.Sdk.Input.MotionSample]::TryWrite 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource+<>c__DisplayClass12_0::<OpenSession>b__0 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::OnReport 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors+SensorEventSink::OnDataUpdated 0x0
  System.Management!dynamicClass::IL_STUB_COMtoCLR 0x0
  coreclr.dll!COMToCLRDispatchHelper
  coreclr.dll!COMToCLRWorker
  coreclr.dll!GenericComCallStub
  SensorsApi.dll!CSensorV2::DataCallback
  ```

- 29 ms (0.5 %) `?!0x0`
- 26 ms (0.4 %)

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

- 24 ms (0.4 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPRecv
  ws2_32.dll!WSARecv
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 23 ms (0.4 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::MaybeAddWorkingWorker 0x0
  System.Private.CoreLib.dll!System.Threading.ThreadPoolWorkQueue::EnqueueAtHighPriority 0x0
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

- 22 ms (0.4 %)

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
  System.Private.CoreLib.dll!System.Threading.Tasks.Task::ExecuteWithThreadLocal 0x0
  System.Private.CoreLib.dll!System.Threading.ThreadPoolWorkQueue::Dispatch 0x0
  ```

- 22 ms (0.4 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  System.Private.CoreLib.dll!Interop+Advapi32::EventWriteTransfer 0x0
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.EventSource::WriteEventWithRelatedActivityIdCore 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Threading.Tasks.VoidTaskResult,WSGM.Device.Msi.Claw8A2Vm.WindowsClawControllerSource+<ReadLoopAsync>d__15]::MoveNext 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.AwaitTaskContinuation::RunOrScheduleAction 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task::RunContinuations 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int32]::SetExistingTaskResult 0x0
  System.Private.CoreLib.dll!System.IO.Strategies.BufferedFileStreamStrategy+<ReadFromNonSeekableAsync>d__36::MoveNext 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunFromThreadPoolDispatchLoop 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int32,System.IO.Strategies.BufferedFileStreamStrategy+<ReadFromNonSeekableAsync>d__36]::ExecuteFromThreadPool 0x0
  System.Private.CoreLib.dll!System.Threading.ThreadPoolWorkQueue::Dispatch 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  ```

- 22 ms (0.4 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 34911 switches (34.3 %)

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

- 17669 switches (17.4 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ```

- 14506 switches (14.2 %)

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

- 13731 switches (13.5 %)

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

- 6080 switches (6.0 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3821 switches (3.8 %)

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

- 1630 switches (1.6 %)

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

- 1610 switches (1.6 %)

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

- 1373 switches (1.3 %)

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

- 1228 switches (1.2 %)

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

- 815 switches (0.8 %)

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

- 365 switches (0.4 %)

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

- 300 switches (0.3 %)

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

- 274 switches (0.3 %)

  ```text
  ntdll.dll!ZwQuerySystemInformation
  KernelBase.dll!GetSystemTimes
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+CpuUtilizationReader::get_CurrentUtilization 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+GateThread::GateThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ```

- 213 switches (0.2 %)

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
  libviiper.dll!0x577D8
  ```

- 193 switches (0.2 %)

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

- 191 switches (0.2 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ```

- 140 switches (0.1 %)

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

- 116 switches (0.1 %)

  ```text
  ntdll.dll!ZwQuerySystemInformation
  KernelBase.dll!GetSystemTimes
  System.Private.CoreLib.dll!Interop+Kernel32::GetSystemTimes 0x0
  System.Private.CoreLib.dll!System.Double System.Diagnostics.Tracing.RuntimeEventSourceHelper::GetCpuUsage()
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.PollingCounter::WritePayload 0x0
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.CounterGroup::OnTimer()
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.CounterGroup::PollForValues()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  ```

- 92 switches (0.1 %)

  ```text
  ntdll.dll!NtAlpcSendWaitReceivePort
  rpcrt4.dll!LRPC_BASE_CCALL::DoSendReceive
  rpcrt4.dll!LRPC_CCALL::SendReceive
  rpcrt4.dll!NdrpClientCall3
  rpcrt4.dll!NdrClientCall3
  MMDevAPI.dll!AudiosrvGetTsAudioProtocol
  MMDevAPI.dll!CDeviceEnumerator::GetDevice
  Windows.Media.Devices.dll!SpatialAudioIO::Initialize
  Windows.Media.Devices.dll!Create_SpatialAudioDeviceStateReader
  Windows.Media.Devices.dll!SpatialAudioDevicePropertyReader::RuntimeClassInitialize
  Windows.Media.Devices.dll!Create_SpatialAudioDevicePropertyReader
  Windows.Media.Devices.dll!SpatialAudioDeviceConfigurationImpl::GetSpatialAudioSettingsAndEncoders
  ```

### Thread 13864 : 755 ms, 0 switches

### Thread 13864 hottest stacks

- 755 ms (100.0 %) `(no stack)`

### Thread 14060 : 503 ms, 35787 switches

### Thread 14060 hottest stacks

- 193 ms (38.3 %)

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

- 171 ms (34.0 %) `(no stack)`
- 67 ms (13.3 %) `(kernel only)`
- 26 ms (5.2 %)

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

### Thread 14060 wait sites

- 34911 switches (97.6 %)

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

- 815 switches (2.3 %)

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

- 28 switches (0.1 %)

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

- 5 switches (0.0 %)

  ```text
  ntdll.dll!RtlSetLastWin32Error
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

### Thread 3288 : 471 ms, 3083 switches

### Thread 3288 hottest stacks

- 192 ms (40.7 %) `(no stack)`
- 81 ms (17.2 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 19 ms (4.1 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 5 ms (1.1 %) `(kernel only)`

### Thread 3288 wait sites

- 3041 switches (98.6 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 11 switches (0.4 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 7 switches (0.2 %)

  ```text
  SensorsApi.dll!operator new
  SensorsApi.dll!CSensorV2::DataCallback
  SensorsApi.dll!wil::ResultFromException<<lambda_54c1951c9aff3eb48ac865ae5274b49d> >
  SensorsApi.dll!CSensorV2::s_DataCallback
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (0.1 %)

  ```text
  rpcrt4.dll!NdrMesTypeDecode2
  rpcrt4.dll!NdrMesTypeDecode3
  SensorsUtilsV2.dll!CollectionsListDeserializeFromBuffer
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 5928 .NET TP Worker: 440 ms, 6216 switches

### Thread 5928 hottest stacks

- 120 ms (27.3 %) `(no stack)`
- 40 ms (9.1 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
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

- 15 ms (3.4 %) `(kernel only)`
- 10 ms (2.3 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
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
  kernel32.dll!BaseThreadInitThunk
  ```

### Thread 5928 wait sites

- 5052 switches (81.3 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ```

- 505 switches (8.1 %)

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

- 187 switches (3.0 %)

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

- 120 switches (1.9 %)

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

### Thread 6676 : 429 ms, 3072 switches

### Thread 6676 hottest stacks

- 169 ms (39.4 %) `(no stack)`
- 64 ms (14.9 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 31 ms (7.2 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::MaybeAddWorkingWorker 0x0
  System.Threading.Channels.dll!System.Threading.Channels.AsyncOperation::SignalCompletion 0x0
  System.Threading.Channels.dll!System.Threading.Channels.ChannelUtilities::DangerousSetOperations 0x0
  System.Threading.Channels.dll!System.Threading.Channels.BoundedChannel`1+BoundedChannelWriter[WSGM.Device.Sdk.Input.MotionSample]::TryWrite 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawMotionSource+<>c__DisplayClass12_0::<OpenSession>b__0 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors::OnReport 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.LegacyPhysicalMotionSensors+SensorEventSink::OnDataUpdated 0x0
  System.Management!dynamicClass::IL_STUB_COMtoCLR 0x0
  coreclr.dll!COMToCLRDispatchHelper
  coreclr.dll!COMToCLRWorker
  coreclr.dll!GenericComCallStub
  SensorsApi.dll!CSensorV2::DataCallback
  ```

- 22 ms (5.1 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 6676 wait sites

- 3039 switches (98.9 %)

  ```text
  ntdll.dll!ZwWaitForMultipleObjects
  KernelBase.dll!WaitForMultipleObjectsEx
  KernelBase.dll!WaitForMultipleObjects
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 13 switches (0.4 %)

  ```text
  ntdll.dll!ZwReadFile
  KernelBase.dll!ReadFile
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 3 switches (0.1 %)

  ```text
  PortableDeviceTypes.dll!DllGetClassObject
  combase.dll!<lambda_05f9514acccb199de55e9edfdf99a0c9>::operator()
  combase.dll!CClassCache::CDllFnPtrMoniker::BindToObject
  combase.dll!CClassCache::SearchForCachedInprocClassOld
  combase.dll!ICoCreateInstanceExWorker
  combase.dll!CComActivator::DoCreateInstance
  combase.dll!CoCreateInstanceExCopy
  combase.dll!CoCreateInstance
  SensorsApi.dll!CSensorV2DataReport::RuntimeClassInitialize
  SensorsApi.dll!CSensorV2::DataCallback
  SensorsApi.dll!wil::ResultFromException<<lambda_54c1951c9aff3eb48ac865ae5274b49d> >
  SensorsApi.dll!CSensorV2::s_DataCallback
  ```

- 2 switches (0.1 %)

  ```text
  ntdll.dll!ZwWaitForAlertByThreadId
  ntdll.dll!RtlpWaitOnCriticalSection
  ntdll.dll!RtlpEnterCriticalSectionContended
  ntdll.dll!RtlEnterCriticalSection
  combase.dll!COleStaticMutexSem::Request
  combase.dll!_CoInitializeEx
  combase.dll!CoInitializeEx
  SensorsApi.dll!CSensorV2::s_DataCallback
  SensorsNativeApi.V2.dll!NativeSensorCollectionNotifThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 16484 .NET TP Worker: 377 ms, 5197 switches

### Thread 16484 hottest stacks

- 119 ms (31.5 %) `(no stack)`
- 32 ms (8.5 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
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

- 10 ms (2.7 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
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
  kernel32.dll!BaseThreadInitThunk
  ```

- 9 ms (2.4 %) `(kernel only)`

### Thread 16484 wait sites

- 4329 switches (83.3 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ```

- 376 switches (7.2 %)

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

- 182 switches (3.5 %)

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

- 60 switches (1.2 %)

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

### Thread 7016 .NET TP Worker: 345 ms, 5343 switches

### Thread 7016 hottest stacks

- 112 ms (32.6 %) `(no stack)`
- 33 ms (9.6 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
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

- 14 ms (4.1 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
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
  kernel32.dll!BaseThreadInitThunk
  ```

- 8 ms (2.3 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  System.Private.CoreLib.dll!Interop+Advapi32::EventWriteTransfer 0x0
  System.Private.CoreLib.dll!System.Diagnostics.Tracing.EventSource::WriteEventWithRelatedActivityIdCore 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task::NewId 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.Task::get_Id 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncMethodBuilderCore::LogTraceOperationBegin 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int32]::GetStateMachineBox 0x0
  System.Private.CoreLib.dll!System.IO.Strategies.BufferedFileStreamStrategy+<ReadFromNonSeekableAsync>d__36::MoveNext 0x0
  System.Private.CoreLib.dll!System.IO.FileStream::ReadAsync 0x0
  WSGM.Device.Msi.Claw8A2Vm!WSGM.Device.Msi.Claw8A2Vm.WindowsClawControllerSource+<ReadLoopAsync>d__15::MoveNext 0x0
  System.Private.CoreLib.dll!System.Threading.ExecutionContext::RunInternal 0x0
  System.Private.CoreLib.dll!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Threading.Tasks.VoidTaskResult,WSGM.Device.Msi.Claw8A2Vm.WindowsClawControllerSource+<ReadLoopAsync>d__15]::MoveNext 0x0
  System.Private.CoreLib.dll!System.Threading.Tasks.AwaitTaskContinuation::RunOrScheduleAction 0x0
  ```

### Thread 7016 wait sites

- 4111 switches (76.9 %)

  ```text
  ntdll.dll!NtRemoveIoCompletion
  KernelBase.dll!GetQueuedCompletionStatus
  System.Private.CoreLib.dll!System.Threading.LowLevelLifoSemaphore::WaitForSignal 0x0
  System.Private.CoreLib.dll!System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()
  System.Private.CoreLib.dll!System.Threading.Thread::StartCallback()
  coreclr.dll!CallDescrWorkerInternal
  coreclr.dll!DispatchCallSimple
  coreclr.dll!KickOffThread_Worker
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!KickOffThread
  kernel32.dll!BaseThreadInitThunk
  ```

- 458 switches (8.6 %)

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

- 363 switches (6.8 %)

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

- 40 switches (0.7 %)

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

### Thread 8124 .NET Tiered Compilation Worker: 337 ms, 111 switches

### Thread 8124 hottest stacks

- 25 ms (7.3 %) `(no stack)`
- 8 ms (2.3 %) `(kernel only)`
- 5 ms (1.5 %)

  ```text
  clrjit.dll!LinearScan::allocateRegisters
  clrjit.dll!LinearScan::doLinearScan
  clrjit.dll!ActionPhase<`Compiler::compCompile'::`2'::<lambda_6> >::DoPhase
  clrjit.dll!Phase::Run
  clrjit.dll!DoPhase<`Compiler::compCompile'::`2'::<lambda_6> >
  clrjit.dll!Compiler::compCompile
  clrjit.dll!Compiler::compCompileHelper
  clrjit.dll!Compiler::compCompile
  clrjit.dll!jitNativeCode
  clrjit.dll!CILJit::compileMethod
  coreclr.dll!UnsafeJitFunctionWorker
  coreclr.dll!UnsafeJitFunction
  coreclr.dll!MethodDesc::JitCompileCodeLocked
  coreclr.dll!MethodDesc::JitCompileCodeLockedEventWrapper
  ```

- 4 ms (1.2 %)

  ```text
  clrjit.dll!Compiler::impImportBlockCode
  clrjit.dll!Compiler::impImportBlock
  clrjit.dll!Compiler::impImport
  clrjit.dll!Compiler::fgImport
  clrjit.dll!Compiler::compCompile
  clrjit.dll!Compiler::compCompileHelper
  clrjit.dll!Compiler::compCompile
  clrjit.dll!jitNativeCode
  clrjit.dll!CILJit::compileMethod
  coreclr.dll!UnsafeJitFunctionWorker
  coreclr.dll!UnsafeJitFunction
  coreclr.dll!MethodDesc::JitCompileCodeLocked
  coreclr.dll!MethodDesc::JitCompileCodeLockedEventWrapper
  coreclr.dll!MethodDesc::JitCompileCode
  ```

### Thread 8124 wait sites

- 53 switches (47.7 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  coreclr.dll!CLREventWaitHelper
  coreclr.dll!TieredCompilationManager::BackgroundWorkerStart
  coreclr.dll!TieredCompilationManager::BackgroundWorkerBootstrapper1
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 39 switches (35.1 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  coreclr.dll!TieredCompilationManager::BackgroundWorkerStart
  coreclr.dll!TieredCompilationManager::BackgroundWorkerBootstrapper1
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (1.8 %)

  ```text
  ntdll.dll!NtDelayExecution
  ntdll.dll!RtlDelayExecution
  KernelBase.dll!SleepEx
  coreclr.dll!TieredCompilationManager::DoBackgroundWork
  coreclr.dll!TieredCompilationManager::BackgroundWorkerStart
  coreclr.dll!TieredCompilationManager::BackgroundWorkerBootstrapper1
  coreclr.dll!ManagedThreadBase_DispatchMiddle
  coreclr.dll!ManagedThreadBase_DispatchOuter
  coreclr.dll!TieredCompilationManager::BackgroundWorkerBootstrapper0
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (0.9 %)

  ```text
  clrjit.dll!Compiler::fgMakeBasicBlocks
  clrjit.dll!Compiler::fgFindBasicBlocks
  clrjit.dll!Compiler::compCompileHelper
  clrjit.dll!Compiler::compCompile
  clrjit.dll!jitNativeCode
  clrjit.dll!CILJit::compileMethod
  coreclr.dll!UnsafeJitFunctionWorker
  coreclr.dll!UnsafeJitFunction
  coreclr.dll!MethodDesc::JitCompileCodeLocked
  coreclr.dll!MethodDesc::JitCompileCodeLockedEventWrapper
  coreclr.dll!MethodDesc::JitCompileCode
  coreclr.dll!MethodDesc::PrepareILBasedCode
  ```

### Thread 9676 : 297 ms, 7457 switches

### Thread 9676 hottest stacks

- 118 ms (39.7 %) `(no stack)`
- 32 ms (10.8 %)

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

- 29 ms (9.8 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 26 ms (8.8 %) `(kernel only)`

### Thread 9676 wait sites

- 3596 switches (48.2 %)

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

- 3235 switches (43.4 %)

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

- 379 switches (5.1 %)

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

- 94 switches (1.3 %)

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

### Thread 2796 : 262 ms, 5935 switches

### Thread 2796 hottest stacks

- 131 ms (50.0 %) `(no stack)`
- 34 ms (13.0 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 24 ms (9.2 %)

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

- 19 ms (7.3 %) `(kernel only)`

### Thread 2796 wait sites

- 2818 switches (47.5 %)

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

- 2622 switches (44.2 %)

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

- 331 switches (5.6 %)

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

- 59 switches (1.0 %)

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

### Thread 16676 : 246 ms, 5621 switches

### Thread 16676 hottest stacks

- 81 ms (32.9 %) `(no stack)`
- 35 ms (14.2 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libviiper.dll!0x12411
  libviiper.dll!0x8B62D
  libviiper.dll!0x8B6A3
  ```

- 31 ms (12.6 %) `(kernel only)`
- 27 ms (11.0 %)

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

### Thread 16676 wait sites

- 2655 switches (47.2 %)

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

- 2492 switches (44.3 %)

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

- 281 switches (5.0 %)

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

- 72 switches (1.3 %)

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

## WSGM.LogonService.exe

### Threads

|  PID |   TID | Name                           | CPU ms | Switches | Switches/s |
| ---: | ----: | ------------------------------ | -----: | -------: | ---------: |
| 2384 | 16132 | .NET Tiered Compilation Worker |      8 |        0 |          0 |
| 2384 |  8820 |                                |      4 |        5 |          0 |
| 2384 |  7604 | .NET Tiered Compilation Worker |      3 |       12 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 12 ms (80.2 %) `(no WSGM frame)`
- 1 ms (6.7 %) `WSGM.LogonService.exe!MethodDesc::GetSig`
- 1 ms (6.7 %) `WSGM.LogonService.exe!Thread::VirtualUnwindCallFrame`
- 1 ms (6.5 %) `WSGM.LogonService.exe!McGenEventWrite_EventWriteTransfer`

### CPU by leaf function

- 11 ms (73.5 %) `?`
- 1 ms (6.7 %) `WSGM.LogonService.exe!MethodDesc::GetSig`
- 1 ms (6.7 %) `ntoskrnl.exe!EtwpGetTraceGuidInfo`
- 1 ms (6.7 %) `ntdll.dll!RtlpUnwindPrologue`
- 1 ms (6.5 %) `ntoskrnl.exe!__memset_spec_ermsb_repmovsb`

### Hottest sampled stacks (leaf first)

- 11 ms (73.5 %) `(no stack)`
- 1 ms (6.7 %)

  ```text
  WSGM.LogonService.exe!MethodDesc::GetSig
  WSGM.LogonService.exe!CEEInfo::getMethodInfoWorker
  WSGM.LogonService.exe!CEEInfo::getMethodInfo
  WSGM.LogonService.exe!`Compiler::impCheckCanInline'::`2'::<lambda_1>::operator()
  WSGM.LogonService.exe!CEEInfo::runWithErrorTrap
  WSGM.LogonService.exe!Compiler::impMarkInlineCandidateHelper
  WSGM.LogonService.exe!Compiler::impMarkInlineCandidate
  WSGM.LogonService.exe!Compiler::impImportCall
  WSGM.LogonService.exe!Compiler::impImportBlockCode
  WSGM.LogonService.exe!Compiler::impImportBlock
  WSGM.LogonService.exe!Compiler::impImport
  WSGM.LogonService.exe!Compiler::fgImport
  WSGM.LogonService.exe!DoPhase
  WSGM.LogonService.exe!Compiler::compCompile
  ```

- 1 ms (6.7 %)

  ```text
  ntdll.dll!ZwTraceControl
  sechost.dll!EnumerateTraceGuidsEx
  ?!0x0
  ```

- 1 ms (6.7 %)

  ```text
  ntdll.dll!RtlpUnwindPrologue
  ntdll.dll!RtlpxVirtualUnwind
  ntdll.dll!RtlVirtualUnwind
  WSGM.LogonService.exe!Thread::VirtualUnwindCallFrame
  WSGM.LogonService.exe!ETW::SamplingLog::SaveCurrentStack
  WSGM.LogonService.exe!ETW::SamplingLog::SendStackTrace
  WSGM.LogonService.exe!EtwCallout
  WSGM.LogonService.exe!McTemplateCoU0xxuhQR3QR3hx_EventWriteTransfer
  WSGM.LogonService.exe!ETW::MethodLog::SendMethodILToNativeMapEvent
  WSGM.LogonService.exe!ETW::MethodLog::MethodJitted
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  WSGM.LogonService.exe!MethodDesc::PrepareILBasedCode
  WSGM.LogonService.exe!TieredCompilationManager::CompileCodeVersion
  ```

- 1 ms (6.5 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  WSGM.LogonService.exe!McGenEventWrite_EventWriteTransfer
  WSGM.LogonService.exe!FireEtwMethodJittingStarted_V1
  WSGM.LogonService.exe!ETW::MethodLog::SendMethodJitStartEvent
  WSGM.LogonService.exe!ETW::MethodLog::MethodJitting
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  WSGM.LogonService.exe!MethodDesc::PrepareILBasedCode
  WSGM.LogonService.exe!TieredCompilationManager::CompileCodeVersion
  WSGM.LogonService.exe!TieredCompilationManager::DoBackgroundWork
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 4 switches (23.5 %) `ntdll.dll!RtlUserThreadStart`
- 2 switches (11.8 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (11.8 %)

  ```text
  WSGM.LogonService.exe!PEDecoder::CheckILMethod
  WSGM.LogonService.exe!Module::GetIL
  WSGM.LogonService.exe!MethodDesc::GetILHeader
  WSGM.LogonService.exe!`anonymous namespace'::GetAndVerifyILHeader
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  WSGM.LogonService.exe!MethodDesc::PrepareILBasedCode
  WSGM.LogonService.exe!TieredCompilationManager::CompileCodeVersion
  WSGM.LogonService.exe!TieredCompilationManager::DoBackgroundWork
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  ```

- 2 switches (11.8 %)

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

- 2 switches (11.8 %)

  ```text
  WSGM.LogonService.exe!LinearScan::allocateRegisters
  WSGM.LogonService.exe!LinearScan::doLinearScan
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  WSGM.LogonService.exe!UnsafeJitFunction
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLocked
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  ```

- 1 switches (5.9 %)

  ```text
  WSGM.LogonService.exe!CorSigUncompressData
  WSGM.LogonService.exe!SigMatchesMethodDesc
  WSGM.LogonService.exe!ReadyToRunInfo::GetPgoInstrumentationData
  WSGM.LogonService.exe!PgoManager::getPgoInstrumentationResultsInstance
  WSGM.LogonService.exe!CEEJitInfo::getPgoInstrumentationResults
  WSGM.LogonService.exe!Compiler::compInitOptions
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  WSGM.LogonService.exe!UnsafeJitFunction
  ```

- 1 switches (5.9 %)

  ```text
  WSGM.LogonService.exe!SubstitutePlaceholdersAndDevirtualizeWalker::LateDevirtualization
  WSGM.LogonService.exe!GenTreeVisitor<SubstitutePlaceholdersAndDevirtualizeWalker>::WalkTree
  WSGM.LogonService.exe!Compiler::fgInline
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

- 1 switches (5.9 %)

  ```text
  WSGM.LogonService.exe!ValueNumStore::VNPUnpackExc
  WSGM.LogonService.exe!Compiler::fgValueNumberTree
  WSGM.LogonService.exe!Compiler::fgValueNumberBlock
  WSGM.LogonService.exe!Compiler::fgValueNumberBlocks
  WSGM.LogonService.exe!Compiler::fgValueNumber
  WSGM.LogonService.exe!DoPhase
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  ```

- 1 switches (5.9 %)

  ```text
  WSGM.LogonService.exe!PEDecoder::CheckILMethod
  WSGM.LogonService.exe!Module::GetIL
  WSGM.LogonService.exe!MethodDesc::GetILHeader
  WSGM.LogonService.exe!CEEInfo::getMethodInfo
  WSGM.LogonService.exe!`Compiler::impCheckCanInline'::`2'::<lambda_1>::operator()
  WSGM.LogonService.exe!CEEInfo::runWithErrorTrap
  WSGM.LogonService.exe!Compiler::impMarkInlineCandidateHelper
  WSGM.LogonService.exe!Compiler::impMarkInlineCandidate
  WSGM.LogonService.exe!Compiler::impImportCall
  WSGM.LogonService.exe!Compiler::impImportBlockCode
  WSGM.LogonService.exe!Compiler::impImportBlock
  WSGM.LogonService.exe!Compiler::impImport
  ```

- 1 switches (5.9 %)

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

### Thread 16132 .NET Tiered Compilation Worker: 8 ms, 0 switches

### Thread 16132 hottest stacks

- 8 ms (100.0 %) `(no stack)`

### Thread 8820 : 4 ms, 5 switches

### Thread 8820 hottest stacks

- 3 ms (75.0 %) `(no stack)`
- 1 ms (25.0 %)

  ```text
  ntdll.dll!ZwTraceControl
  sechost.dll!EnumerateTraceGuidsEx
  ?!0x0
  ```

### Thread 8820 wait sites

- 3 switches (60.0 %) `ntdll.dll!RtlUserThreadStart`
- 2 switches (40.0 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 7604 .NET Tiered Compilation Worker: 3 ms, 12 switches

### Thread 7604 hottest stacks

- 1 ms (33.7 %)

  ```text
  WSGM.LogonService.exe!MethodDesc::GetSig
  WSGM.LogonService.exe!CEEInfo::getMethodInfoWorker
  WSGM.LogonService.exe!CEEInfo::getMethodInfo
  WSGM.LogonService.exe!`Compiler::impCheckCanInline'::`2'::<lambda_1>::operator()
  WSGM.LogonService.exe!CEEInfo::runWithErrorTrap
  WSGM.LogonService.exe!Compiler::impMarkInlineCandidateHelper
  WSGM.LogonService.exe!Compiler::impMarkInlineCandidate
  WSGM.LogonService.exe!Compiler::impImportCall
  WSGM.LogonService.exe!Compiler::impImportBlockCode
  WSGM.LogonService.exe!Compiler::impImportBlock
  WSGM.LogonService.exe!Compiler::impImport
  WSGM.LogonService.exe!Compiler::fgImport
  WSGM.LogonService.exe!DoPhase
  WSGM.LogonService.exe!Compiler::compCompile
  ```

- 1 ms (33.7 %)

  ```text
  ntdll.dll!RtlpUnwindPrologue
  ntdll.dll!RtlpxVirtualUnwind
  ntdll.dll!RtlVirtualUnwind
  WSGM.LogonService.exe!Thread::VirtualUnwindCallFrame
  WSGM.LogonService.exe!ETW::SamplingLog::SaveCurrentStack
  WSGM.LogonService.exe!ETW::SamplingLog::SendStackTrace
  WSGM.LogonService.exe!EtwCallout
  WSGM.LogonService.exe!McTemplateCoU0xxuhQR3QR3hx_EventWriteTransfer
  WSGM.LogonService.exe!ETW::MethodLog::SendMethodILToNativeMapEvent
  WSGM.LogonService.exe!ETW::MethodLog::MethodJitted
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  WSGM.LogonService.exe!MethodDesc::PrepareILBasedCode
  WSGM.LogonService.exe!TieredCompilationManager::CompileCodeVersion
  ```

- 1 ms (32.5 %)

  ```text
  ntdll.dll!ZwTraceEvent
  ntdll.dll!EtwEventWriteTransfer
  WSGM.LogonService.exe!McGenEventWrite_EventWriteTransfer
  WSGM.LogonService.exe!FireEtwMethodJittingStarted_V1
  WSGM.LogonService.exe!ETW::MethodLog::SendMethodJitStartEvent
  WSGM.LogonService.exe!ETW::MethodLog::MethodJitting
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  WSGM.LogonService.exe!MethodDesc::PrepareILBasedCode
  WSGM.LogonService.exe!TieredCompilationManager::CompileCodeVersion
  WSGM.LogonService.exe!TieredCompilationManager::DoBackgroundWork
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  ```

### Thread 7604 wait sites

- 2 switches (16.7 %)

  ```text
  WSGM.LogonService.exe!PEDecoder::CheckILMethod
  WSGM.LogonService.exe!Module::GetIL
  WSGM.LogonService.exe!MethodDesc::GetILHeader
  WSGM.LogonService.exe!`anonymous namespace'::GetAndVerifyILHeader
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  WSGM.LogonService.exe!MethodDesc::PrepareILBasedCode
  WSGM.LogonService.exe!TieredCompilationManager::CompileCodeVersion
  WSGM.LogonService.exe!TieredCompilationManager::DoBackgroundWork
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerStart
  WSGM.LogonService.exe!TieredCompilationManager::BackgroundWorkerBootstrapper1
  WSGM.LogonService.exe!ManagedThreadBase_DispatchMiddle
  ```

- 2 switches (16.7 %)

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

- 2 switches (16.7 %)

  ```text
  WSGM.LogonService.exe!LinearScan::allocateRegisters
  WSGM.LogonService.exe!LinearScan::doLinearScan
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  WSGM.LogonService.exe!UnsafeJitFunction
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLocked
  WSGM.LogonService.exe!MethodDesc::JitCompileCodeLockedEventWrapper
  WSGM.LogonService.exe!MethodDesc::JitCompileCode
  ```

- 1 switches (8.3 %)

  ```text
  WSGM.LogonService.exe!CorSigUncompressData
  WSGM.LogonService.exe!SigMatchesMethodDesc
  WSGM.LogonService.exe!ReadyToRunInfo::GetPgoInstrumentationData
  WSGM.LogonService.exe!PgoManager::getPgoInstrumentationResultsInstance
  WSGM.LogonService.exe!CEEJitInfo::getPgoInstrumentationResults
  WSGM.LogonService.exe!Compiler::compInitOptions
  WSGM.LogonService.exe!Compiler::compCompileHelper
  WSGM.LogonService.exe!Compiler::compCompile
  WSGM.LogonService.exe!jitNativeCode
  WSGM.LogonService.exe!CILJit::compileMethod
  WSGM.LogonService.exe!UnsafeJitFunctionWorker
  WSGM.LogonService.exe!UnsafeJitFunction
  ```

## steam.exe

### Threads

|   PID |   TID | Name                            | CPU ms | Switches | Switches/s |
| ----: | ----: | ------------------------------- | -----: | -------: | ---------: |
| 13984 |  3648 | CSteamController::CHIDIO Thread |    515 |     7745 |        140 |
| 13984 | 12076 |                                 |    246 |     3519 |         64 |
| 13984 |  2996 | IPC:CSteamEngine                |    140 |     1791 |         32 |
| 13984 |  9872 |                                 |      8 |      216 |          4 |
| 13984 | 11672 | CHTTPClientThreadPool:0         |      6 |      163 |          3 |
| 13984 | 11144 |                                 |      6 |      197 |          4 |
| 13984 | 16084 | CJobMgr::m_WorkThreadPool:1     |      5 |      153 |          3 |
| 13984 |  2520 | CJobMgr::m_WorkThreadPool:0     |      5 |      150 |          3 |
| 13984 |  9900 | CJobMgr::m_WorkThreadPool:0     |      5 |      144 |          3 |
| 13984 | 15248 |                                 |      5 |      196 |          4 |
| 13984 |  2832 | Controller Work Item Thread     |      4 |       32 |          1 |
| 13984 | 14032 | CNet Encrypt:0                  |      4 |      151 |          3 |
| 13984 | 11464 | CHTTPClientThreadPool:0         |      4 |      156 |          3 |
| 13984 | 15548 | IOCP Thread 0                   |      4 |       85 |          2 |
| 13984 | 14208 | CRemoteClientSoftAP::m_pWorkThr |      3 |      142 |          3 |
| 13984 | 15004 | CJobMgr::m_WorkThreadPool:3     |      3 |      164 |          3 |
| 13984 | 16652 | CJobMgr::m_WorkThreadPool:1     |      2 |      145 |          3 |
| 13984 | 16724 | CNet Encrypt:0                  |      2 |      143 |          3 |
| 13984 | 15408 | CHTTPCacheFileThreadPool:0      |      2 |      151 |          3 |
| 13984 |  9288 | CJobMgr::m_WorkThreadPool:2     |      2 |      145 |          3 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 973 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 352 ms (36.2 %) `?`
- 66 ms (6.8 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 26 ms (2.7 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 17 ms (1.7 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 8 ms (0.8 %) `ntoskrnl.exe!SwapContext`
- 8 ms (0.8 %) `win32kfull.sys!xxxRealInternalGetMessage`
- 7 ms (0.7 %) `ntoskrnl.exe!RtlpWalkFrameChain`
- 7 ms (0.7 %) `tier0_s64.dll!0x1322F`
- 5 ms (0.5 %) `steamclient64.dll!0x65C47B`
- 5 ms (0.5 %) `ntdll.dll!RtlQueryPerformanceCounter`
- 5 ms (0.5 %) `ntoskrnl.exe!KiCancelTimer`
- 4 ms (0.4 %) `ntoskrnl.exe!KiAbProcessPostContextSwitch`
- 4 ms (0.4 %) `win32kbase.sys!EtwTraceAcquiredExclusiveUserCrit`
- 4 ms (0.4 %) `ntoskrnl.exe!EtwpReserveTraceBuffer`
- 4 ms (0.4 %) `ntoskrnl.exe!KiSystemServiceRepeat`
- 4 ms (0.4 %) `ntoskrnl.exe!KiSystemServiceUser`
- 4 ms (0.4 %) `ntoskrnl.exe!KiInsertTimerTable`
- 3 ms (0.3 %) `tier0_s64.dll!0x11F6E`
- 3 ms (0.3 %) `ntoskrnl.exe!__chkstk`
- 3 ms (0.3 %) `ntoskrnl.exe!MiReleasePtes`

### Hottest sampled stacks (leaf first)

- 352 ms (36.2 %) `(no stack)`
- 74 ms (7.6 %)

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

- 51 ms (5.2 %)

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

- 36 ms (3.7 %) `(kernel only)`
- 25 ms (2.6 %)

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

- 18 ms (1.9 %)

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

- 14 ms (1.4 %)

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

- 12 ms (1.2 %)

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

- 7 ms (0.7 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x9700B3
  steamclient64.dll!0x89F5DB
  steamclient64.dll!0x8A00CE
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

- 7 ms (0.7 %)

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

- 7 ms (0.7 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x9700B3
  steamclient64.dll!0x8EFA14
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x79BBF7
  SteamUI.dll!0x5B7F79
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  ```

- 6 ms (0.6 %)

  ```text
  win32u.dll!ZwUserGetCursorInfo
  SteamUI.dll!0x6CDCA3
  SteamUI.dll!0x596DFA
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

- 6 ms (0.6 %)

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

- 5 ms (0.5 %)

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

- 5 ms (0.5 %)

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

- 5 ms (0.5 %)

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

- 4 ms (0.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  SDL3.dll!0xD76F4
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

- 4 ms (0.4 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0x96FEFD
  steamclient64.dll!0x8EFA7F
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x79BBF7
  SteamUI.dll!0x5B7F79
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  ```

- 4 ms (0.4 %)

  ```text
  steamclient64.dll!0x65C47B
  steamclient64.dll!0x60D8D5
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

- 4 ms (0.4 %)

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

### Wakeups by wait site (switch-in stack, leaf first)

- 5563 switches (35.1 %)

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

- 2044 switches (12.9 %)

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

- 1340 switches (8.4 %)

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

- 1175 switches (7.4 %)

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

- 1006 switches (6.3 %)

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

- 576 switches (3.6 %)

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

- 484 switches (3.1 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0x96FEFD
  steamclient64.dll!0x8EFA7F
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x79BBF7
  SteamUI.dll!0x5B7F79
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  ```

- 480 switches (3.0 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0x96FEFD
  steamclient64.dll!0x8EFA7F
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x84B9E7
  SteamUI.dll!0x5B7FBA
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  ```

- 359 switches (2.3 %)

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

- 318 switches (2.0 %)

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

- 294 switches (1.9 %)

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

- 240 switches (1.5 %)

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

- 205 switches (1.3 %)

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

- 189 switches (1.2 %)

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

- 188 switches (1.2 %)

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

- 151 switches (1.0 %)

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

- 125 switches (0.8 %)

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

- 84 switches (0.5 %)

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

- 75 switches (0.5 %)

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

- 72 switches (0.5 %)

  ```text
  win32u.dll!ZwUserGetCursorInfo
  SteamUI.dll!0x6CDCA3
  SteamUI.dll!0x596DFA
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

### Thread 3648 CSteamController::CHIDIO Thread: 515 ms, 7745 switches

### Thread 3648 hottest stacks

- 186 ms (36.1 %) `(no stack)`
- 74 ms (14.4 %)

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

- 51 ms (9.9 %)

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

- 18 ms (3.5 %)

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

### Thread 3648 wait sites

- 5563 switches (71.8 %)

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

- 2044 switches (26.4 %)

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

- 125 switches (1.6 %)

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

- 2 switches (0.0 %)

  ```text
  SDL3.dll!0x18E930
  SDL3.dll!0x110FE2
  SDL3.dll!0xF6944
  SDL3.dll!0x10A9EB
  SDL3.dll!0x116ED0
  steamclient64.dll!0x6D1B83
  steamclient64.dll!0x61393A
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

### Thread 12076 : 246 ms, 3519 switches

### Thread 12076 hottest stacks

- 98 ms (39.9 %) `(no stack)`
- 25 ms (10.2 %)

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

- 7 ms (2.8 %)

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

- 7 ms (2.8 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x9700B3
  steamclient64.dll!0x8EFA14
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x79BBF7
  SteamUI.dll!0x5B7F79
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  SteamUI.dll!0x595D6C
  tier0_s64.dll!0xC669
  ```

### Thread 12076 wait sites

- 1175 switches (33.4 %)

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

- 484 switches (13.8 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0x96FEFD
  steamclient64.dll!0x8EFA7F
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x79BBF7
  SteamUI.dll!0x5B7F79
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  ```

- 480 switches (13.6 %)

  ```text
  ntdll.dll!ZwWaitForSingleObject
  KernelBase.dll!WaitForSingleObjectEx
  tier0_s64.dll!0x11FA6
  steamclient64.dll!0x96FEFD
  steamclient64.dll!0x8EFA7F
  steamclient64.dll!0x89CDC0
  steamclient64.dll!0x84B9E7
  SteamUI.dll!0x5B7FBA
  SteamUI.dll!0x90EE1E
  SteamUI.dll!0x603F50
  SteamUI.dll!0x593070
  SteamUI.dll!0x596E73
  ```

- 359 switches (10.2 %)

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

### Thread 2996 IPC:CSteamEngine: 140 ms, 1791 switches

### Thread 2996 hottest stacks

- 50 ms (35.8 %) `(no stack)`
- 14 ms (10.0 %)

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

- 7 ms (5.0 %) `(kernel only)`
- 7 ms (5.0 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x9700B3
  steamclient64.dll!0x89F5DB
  steamclient64.dll!0x8A00CE
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

### Thread 2996 wait sites

- 1340 switches (74.8 %)

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

- 42 switches (2.3 %)

  ```text
  ntdll.dll!NtSetEvent
  KernelBase.dll!SetEvent
  tier0_s64.dll!0x113EE
  steamclient64.dll!0x9700B3
  steamclient64.dll!0x89F5DB
  steamclient64.dll!0x8A00CE
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  ```

- 20 switches (1.1 %)

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

- 16 switches (0.9 %)

  ```text
  ntdll.dll!ZwClearEvent
  KernelBase.dll!ResetEvent
  tier0_s64.dll!0x1137E
  steamclient64.dll!0x8A00C5
  steamclient64.dll!0x89F636
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ```

### Thread 9872 : 8 ms, 216 switches

### Thread 9872 hottest stacks

- 5 ms (62.5 %)

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

- 1 ms (12.5 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  steamclient64.dll!0xE64765
  steamclient64.dll!0xE6BF26
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

- 1 ms (12.5 %) `(no stack)`

### Thread 9872 wait sites

- 205 switches (94.9 %)

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

- 3 switches (1.4 %)

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

- 2 switches (0.9 %) `(kernel only)`
- 2 switches (0.9 %)

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

### Thread 11672 CHTTPClientThreadPool:0: 6 ms, 163 switches

### Thread 11672 hottest stacks

- 3 ms (50.0 %) `(no stack)`
- 1 ms (16.7 %)

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

- 1 ms (16.7 %) `(kernel only)`
- 1 ms (16.7 %)

  ```text
  steamclient64.dll!0x1164FCA
  steamclient64.dll!0x103F11C
  steamclient64.dll!0x103CA90
  steamclient64.dll!0x113AC09
  steamclient64.dll!0x113C5C5
  steamclient64.dll!0x11196D0
  steamclient64.dll!0x1138239
  steamclient64.dll!0x11381AA
  steamclient64.dll!0x110D50E
  steamclient64.dll!0x110BD29
  steamclient64.dll!0xE8AE84
  steamclient64.dll!0xE8B6B7
  steamclient64.dll!0xE89897
  steamclient64.dll!0xD913DB
  ```

### Thread 11672 wait sites

- 154 switches (94.5 %)

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

- 3 switches (1.8 %)

  ```text
  steamclient64.dll!0x1067EDC
  steamclient64.dll!0x1040A89
  steamclient64.dll!0x103D14D
  steamclient64.dll!0x113ABE5
  steamclient64.dll!0x113C5C5
  steamclient64.dll!0x11196D0
  steamclient64.dll!0x1138239
  steamclient64.dll!0x11381AA
  steamclient64.dll!0x110D50E
  steamclient64.dll!0x110BD29
  steamclient64.dll!0xE8AE84
  steamclient64.dll!0xE8B6B7
  ```

- 1 switches (0.6 %)

  ```text
  tier0_s64.dll!0x20B80
  tier0_s64.dll!0x806E
  steamclient64.dll!0xE9221A
  steamclient64.dll!0x1036183
  steamclient64.dll!0x112B73C
  steamclient64.dll!0x113AC25
  steamclient64.dll!0x113C5C5
  steamclient64.dll!0x11196D0
  steamclient64.dll!0x1138239
  steamclient64.dll!0x11381AA
  steamclient64.dll!0x110D50E
  steamclient64.dll!0x110BD29
  ```

- 1 switches (0.6 %)

  ```text
  steamclient64.dll!0x112ACF3
  steamclient64.dll!0x113AC8A
  steamclient64.dll!0x113C5C5
  steamclient64.dll!0x11196D0
  steamclient64.dll!0x1138239
  steamclient64.dll!0x11381AA
  steamclient64.dll!0x110D50E
  steamclient64.dll!0x110BD29
  steamclient64.dll!0xE8AE84
  steamclient64.dll!0xE8B6B7
  steamclient64.dll!0xE89897
  steamclient64.dll!0xD913DB
  ```

### Thread 11144 : 6 ms, 197 switches

### Thread 11144 hottest stacks

- 4 ms (66.7 %)

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

- 2 ms (33.3 %) `(no stack)`

### Thread 11144 wait sites

- 189 switches (95.9 %)

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

- 1 switches (0.5 %)

  ```text
  kernel32.dll!WaitForSingleObject
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

- 1 switches (0.5 %)

  ```text
  SteamUI.dll!0x92B94D
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
  tier0_s64.dll!0x1AD08
  SteamUI.dll!0x92B946
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 16084 CJobMgr::m_WorkThreadPool:1: 5 ms, 153 switches

### Thread 16084 hottest stacks

- 4 ms (80.0 %)

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

- 1 ms (20.0 %) `(no stack)`

### Thread 16084 wait sites

- 143 switches (93.5 %)

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
  tier0_s64.dll!0x1AEBA
  tier0_s64.dll!0x1AD3C
  steamclient64.dll!0xD9126B
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
  tier0_s64.dll!0x11FAB
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
  steamclient64.dll!0xD90603
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

### Thread 2520 CJobMgr::m_WorkThreadPool:0: 5 ms, 150 switches

### Thread 2520 hottest stacks

- 2 ms (40.0 %)

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

- 2 ms (40.0 %) `(no stack)`
- 1 ms (20.0 %)

  ```text
  tier0_s64.dll!0x10CD0
  steamclient64.dll!0xD9125F
  tier0_s64.dll!0x119CF
  tier0_s64.dll!0xC5DF
  tier0_s64.dll!0xC669
  tier0_s64.dll!0xBE62
  tier0_s64.dll!0xD10F
  tier0_s64.dll!0x11BF1
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 2520 wait sites

- 144 switches (96.0 %)

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

- 3 switches (2.0 %)

  ```text
  steamclient64.dll!0xD90648
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

- 2 switches (1.3 %)

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

- 1 switches (0.7 %)

  ```text
  tier0_s64.dll!0x1AD08
  steamclient64.dll!0xD9126B
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

|   PID |   TID | Name                                                  | CPU ms | Switches | Switches/s |
| ----: | ----: | ----------------------------------------------------- | -----: | -------: | ---------: |
|  1612 |  8432 | CrBrowserMain                                         |    239 |     3512 |         64 |
|  7860 | 17044 | CrRendererMain                                        |     20 |       58 |          1 |
|  1612 |  4216 | ThreadPoolSingleThreadCOMSTASharedForegroundBlocking0 |     16 |      713 |         13 |
|  1612 |  5176 | Chrome_IOThread                                       |     12 |      108 |          2 |
|  1804 | 16364 | CrGpuMain                                             |     11 |       19 |          0 |
|  1612 |  2568 | Chrome_DevToolsHandlerThread                          |      9 |       51 |          1 |
|  1612 |   220 | ThreadPoolForegroundWorker                            |      6 |       33 |          1 |
|  1612 |  7568 | ThreadPoolServiceThread                               |      5 |       75 |          1 |
| 16360 |  3244 | Chrome_ChildIOThread                                  |      5 |       25 |          0 |
|  7860 |  9592 | Chrome_ChildIOThread                                  |      4 |       59 |          1 |
| 16360 | 15464 | network.CrUtilityMain                                 |      4 |       21 |          0 |
|  4480 | 12284 | audio.CrUtilityMain                                   |      3 |       14 |          0 |
|  1804 | 12028 | StackSamplingProfiler                                 |      3 |        0 |          0 |
|  1804 | 16316 |                                                       |      2 |        7 |          0 |
|  1612 |  4556 | ThreadPoolBackgroundWorker                            |      2 |       22 |          0 |
|  1612 |  4512 |                                                       |      2 |       41 |          1 |
|  1612 | 11612 |                                                       |      2 |       11 |          0 |
| 16360 | 15928 |                                                       |      1 |        6 |          0 |
|  4812 | 15788 | storage.CrUtilityMain                                 |      1 |       12 |          0 |
|  4480 |  2556 | HangWatcher                                           |      1 |        3 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 353 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 118 ms (33.4 %) `?`
- 25 ms (7.1 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 19 ms (5.3 %) `libcef.dll!0x142076B`
- 8 ms (2.3 %) `ntoskrnl.exe!RtlpUnwindPrologue`
- 6 ms (1.7 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 6 ms (1.7 %) `ntoskrnl.exe!SwapContext`
- 5 ms (1.4 %) `ntoskrnl.exe!RtlpWalkFrameChain`
- 3 ms (0.9 %) `libcef.dll!0x35229E2`
- 3 ms (0.9 %) `ntoskrnl.exe!RtlpLookupUserFunctionTableInverted`
- 2 ms (0.6 %) `ntoskrnl.exe!MiUnlinkFreeOrZeroedPage`
- 2 ms (0.6 %) `ntoskrnl.exe!RtlpxLookupFunctionTable`
- 2 ms (0.6 %) `win32u.dll!ZwUserPeekMessage`
- 2 ms (0.6 %) `ntoskrnl.exe!KiHeteroSelectProcessorToPreempt`
- 2 ms (0.6 %) `libcef.dll!0x34FC780`
- 2 ms (0.6 %) `steamwebhelper.exe!0x173C7B`
- 2 ms (0.6 %) `ntoskrnl.exe!ExpReleaseFastResourceExclusiveSlow`
- 2 ms (0.6 %) `ntoskrnl.exe!EtwpTraceStackWalk`
- 2 ms (0.6 %) `SDL3.dll!0x1AF3A3`
- 2 ms (0.6 %) `user32.dll!GetQueueStatus`
- 2 ms (0.6 %) `ntoskrnl.exe!KiUpdateSpeculationControl`

### Hottest sampled stacks (leaf first)

- 118 ms (33.4 %) `(no stack)`
- 54 ms (15.3 %)

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

- 13 ms (3.7 %)

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

- 12 ms (3.4 %) `(kernel only)`
- 12 ms (3.3 %)

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

- 11 ms (3.1 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  libcef.dll!0x1EF142A
  libcef.dll!0x39AB2ED
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 6 ms (1.7 %)

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

- 2 ms (0.6 %)

  ```text
  ntdll.dll!NtFreeVirtualMemory
  KernelBase.dll!VirtualFree
  libcef.dll!0x1901060
  libcef.dll!0x1913F9A
  libcef.dll!0x352DBC0
  libcef.dll!0x34F51AB
  libcef.dll!0x73BE1E7
  libcef.dll!0x73BDFD0
  libcef.dll!0x73BD84A
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

- 2 ms (0.6 %)

  ```text
  ntdll.dll!ZwWriteFile
  KernelBase.dll!WriteFile
  libcef.dll!0x1A867AA
  libcef.dll!0x1A8CE49
  libcef.dll!0x1A92382
  libcef.dll!0x1A979B0
  libcef.dll!0x36CBA36
  libcef.dll!0x36BCAC1
  libcef.dll!0x1623FA8
  libcef.dll!0x1624109
  libcef.dll!0x1B7BAE4
  libcef.dll!0x1B7A27F
  libcef.dll!0x1B7AF89
  libcef.dll!0x7AFCE22
  ```

- 2 ms (0.6 %)

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

- 2 ms (0.6 %)

  ```text
  steamwebhelper.exe!0x173C7B
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

- 2 ms (0.6 %)

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
  ntdll.dll!RtlUserThreadStart
  ```

- 2 ms (0.6 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libcef.dll!0x196254B
  libcef.dll!0x19639DA
  libcef.dll!0x25C8F08
  libcef.dll!0x25C8B79
  libcef.dll!0x191E662
  libcef.dll!0x19205B0
  libcef.dll!0x1921B53
  libcef.dll!0x192184E
  libcef.dll!0x1427351
  libcef.dll!0x1F29152
  libcef.dll!0x1F2903F
  ```

- 2 ms (0.6 %)

  ```text
  SDL3.dll!0x1AF3A3
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

- 2 ms (0.6 %)

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

- 2 ms (0.6 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libcef.dll!0x196254B
  libcef.dll!0x19639DA
  libcef.dll!0x1760A37
  libcef.dll!0x1BF8DED
  libcef.dll!0x1760839
  libcef.dll!0xE7672D
  libcef.dll!0xE7C785
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  ```

- 2 ms (0.6 %)

  ```text
  user32.dll!GetQueueStatus
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

- 2 ms (0.6 %)

  ```text
  libcef.dll!0x35229E2
  libcef.dll!0x362E9F3
  libcef.dll!0x13D2A94
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

- 1 ms (0.3 %)

  ```text
  igd10umt64xe.dll!0xFE5931
  igd10umt64xe.dll!0x3F7551
  igd10umt64xe.dll!0xFDFA1A
  igd10umt64xe.dll!0xA741E6
  igd10umt64xe.dll!0x936868
  igd10umt64xe.dll!0x170F08
  igd10umt64xe.dll!0x170E14
  igd10umt64xe.dll!0xFDFA1A
  igd10umt64xe.dll!0x83188
  igd10umt64xe.dll!0x74C32
  igd10umt64xe.dll!0xF2CA3
  igd10umt64xe.dll!0xF303A
  igd10umt64xe.dll!0x1F426B
  kernel32.dll!BaseThreadInitThunk
  ```

- 1 ms (0.3 %)

  ```text
  tier0_s64.dll!0x26C17
  tier0_s64.dll!0x20E15
  tier0_s64.dll!0x7FB9
  steamwebhelper.exe!0x211616
  steamwebhelper.exe!0x25AB54
  steamwebhelper.exe!0x18C02E
  steamwebhelper.exe!0x18D603
  steamwebhelper.exe!0x1B174C
  steamwebhelper.exe!0x18D498
  steamwebhelper.exe!0x1B174C
  steamwebhelper.exe!0x18BFC2
  steamwebhelper.exe!0x18D603
  steamwebhelper.exe!0x1B174C
  steamwebhelper.exe!0x190E5F
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 2670 switches (54.8 %)

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

- 776 switches (15.9 %)

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

- 521 switches (10.7 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  libcef.dll!0x1EF142A
  libcef.dll!0x39AB2ED
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 175 switches (3.6 %)

  ```text
  win32u.dll!ZwUserMsgWaitForMultipleObjectsEx
  libcef.dll!0x1EF1870
  libcef.dll!0x39AAF61
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 105 switches (2.2 %)

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

- 82 switches (1.7 %)

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

- 70 switches (1.4 %)

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

- 54 switches (1.1 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 41 switches (0.8 %)

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

- 36 switches (0.7 %)

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

- 32 switches (0.7 %)

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

- 26 switches (0.5 %)

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

- 20 switches (0.4 %)

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

- 17 switches (0.3 %)

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

- 16 switches (0.3 %)

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

- 13 switches (0.3 %)

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

- 9 switches (0.2 %)

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
  ```

- 8 switches (0.2 %)

  ```text
  win32u.dll!ZwUserPostMessage
  user32.dll!PostMessageW
  libcef.dll!0x34FD534
  libcef.dll!0x362E701
  libcef.dll!0x36282FE
  libcef.dll!0x3505D6B
  libcef.dll!0xE7A038
  libcef.dll!0x1761363
  libcef.dll!0x176117E
  libcef.dll!0x3655E77
  libcef.dll!0x3655BB0
  libcef.dll!0x365576E
  ```

- 8 switches (0.2 %)

  ```text
  win32u.dll!ZwUserGetClassName
  user32.dll!GetClassNameW
  libcef.dll!0x158DC8C
  libcef.dll!0x1EE9108
  libcef.dll!0x1EEA00A
  libcef.dll!0x1EE9FAB
  user32.dll!EnumWindows
  libcef.dll!0x1EEA773
  libcef.dll!0x13E868D
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  ```

- 6 switches (0.1 %) `ntdll.dll!RtlUserThreadStart`

### Thread 8432 CrBrowserMain: 239 ms, 3512 switches

### Thread 8432 hottest stacks

- 85 ms (35.6 %) `(no stack)`
- 54 ms (22.6 %)

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

- 13 ms (5.4 %)

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

- 12 ms (4.9 %)

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

### Thread 8432 wait sites

- 2670 switches (76.0 %)

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

- 776 switches (22.1 %)

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

- 9 switches (0.3 %)

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
  ```

- 6 switches (0.2 %)

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

### Thread 17044 CrRendererMain: 20 ms, 58 switches

### Thread 17044 hottest stacks

- 6 ms (29.6 %) `(no stack)`
- 2 ms (10.3 %)

  ```text
  ntdll.dll!NtFreeVirtualMemory
  KernelBase.dll!VirtualFree
  libcef.dll!0x1901060
  libcef.dll!0x1913F9A
  libcef.dll!0x352DBC0
  libcef.dll!0x34F51AB
  libcef.dll!0x73BE1E7
  libcef.dll!0x73BDFD0
  libcef.dll!0x73BD84A
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

- 2 ms (10.0 %)

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

- 1 ms (5.0 %)

  ```text
  tier0_s64.dll!0x26C17
  tier0_s64.dll!0x20E15
  tier0_s64.dll!0x7FB9
  steamwebhelper.exe!0x211616
  steamwebhelper.exe!0x25AB54
  steamwebhelper.exe!0x18C02E
  steamwebhelper.exe!0x18D603
  steamwebhelper.exe!0x1B174C
  steamwebhelper.exe!0x18D498
  steamwebhelper.exe!0x1B174C
  steamwebhelper.exe!0x18BFC2
  steamwebhelper.exe!0x18D603
  steamwebhelper.exe!0x1B174C
  steamwebhelper.exe!0x190E5F
  ```

### Thread 17044 wait sites

- 36 switches (62.1 %)

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

- 5 switches (8.6 %)

  ```text
  ntdll.dll!NtSetIoCompletion
  KernelBase.dll!PostQueuedCompletionStatus
  libcef.dll!0x34FDBE4
  libcef.dll!0x362E701
  libcef.dll!0x36282FE
  libcef.dll!0x3505D6B
  libcef.dll!0x359AC5C
  libcef.dll!0x3554234
  libcef.dll!0x3559190
  libcef.dll!0x124D166
  libcef.dll!0x234000F
  libcef.dll!0x23401E8
  ```

- 2 switches (3.4 %)

  ```text
  libcef.dll!0x98A6D7
  libcef.dll!0x98AC84
  libcef.dll!0x832A4C
  libcef.dll!0x832EAA
  libcef.dll!0x83A804
  libcef.dll!0x83A686
  libcef.dll!0x863E05
  libcef.dll!0x8668E1
  libcef.dll!0xC0DDD7
  libcef.dll!0xBFAE8F
  libcef.dll!0xC159B4
  libcef.dll!0xC0CDD7
  ```

- 2 switches (3.4 %)

  ```text
  libcef.dll!0x38C75F5
  libcef.dll!0x34DC2B7
  libcef.dll!0x2E43BA3
  libcef.dll!0x34DC451
  libcef.dll!0x34DD952
  libcef.dll!0x34DFB07
  libcef.dll!0x34E0E18
  libcef.dll!0x362CDA1
  libcef.dll!0x350890B
  libcef.dll!0x362F107
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  ```

### Thread 4216 ThreadPoolSingleThreadCOMSTASharedForegroundBlocking0: 16 ms, 713 switches

### Thread 4216 hottest stacks

- 11 ms (68.9 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  libcef.dll!0x1EF142A
  libcef.dll!0x39AB2ED
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (6.2 %)

  ```text
  win32u.dll!ZwUserPostMessage
  user32.dll!PostMessageW
  libcef.dll!0x34FD534
  libcef.dll!0x362E701
  libcef.dll!0x36282FE
  libcef.dll!0x3505D6B
  libcef.dll!0x1EEA8B3
  libcef.dll!0x13E868D
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  libcef.dll!0x39AB330
  ```

- 1 ms (6.2 %)

  ```text
  libcef.dll!0x35058A2
  libcef.dll!0x39AB297
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (6.2 %)

  ```text
  ntdll.dll!NtCallbackReturn
  user32.dll!__ClientCallWinEventProc
  ntdll.dll!KiUserCallbackDispatcherContinue
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  libcef.dll!0x1EF142A
  libcef.dll!0x39AB2ED
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 4216 wait sites

- 521 switches (73.1 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageW
  libcef.dll!0x1EF142A
  libcef.dll!0x39AB2ED
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 175 switches (24.5 %)

  ```text
  win32u.dll!ZwUserMsgWaitForMultipleObjectsEx
  libcef.dll!0x1EF1870
  libcef.dll!0x39AAF61
  libcef.dll!0x25D4598
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 8 switches (1.1 %)

  ```text
  win32u.dll!ZwUserGetClassName
  user32.dll!GetClassNameW
  libcef.dll!0x158DC8C
  libcef.dll!0x1EE9108
  libcef.dll!0x1EEA00A
  libcef.dll!0x1EE9FAB
  user32.dll!EnumWindows
  libcef.dll!0x1EEA773
  libcef.dll!0x13E868D
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  ```

- 5 switches (0.7 %)

  ```text
  win32u.dll!ZwUserGetWindowPlacement
  libcef.dll!0x1EE9292
  libcef.dll!0x1EEA00A
  libcef.dll!0x1EE9FAB
  user32.dll!EnumWindows
  libcef.dll!0x1EEA773
  libcef.dll!0x13E868D
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  ```

### Thread 5176 Chrome_IOThread: 12 ms, 108 switches

### Thread 5176 hottest stacks

- 5 ms (41.7 %) `(no stack)`
- 2 ms (16.7 %)

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
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (8.3 %)

  ```text
  ws2_32.dll!WahReferenceContextByHandle
  mswsock.dll!SockFindAndReferenceSocket
  mswsock.dll!WSPRecvFrom
  ws2_32.dll!WSARecvFrom
  libcef.dll!0x195FDE5
  libcef.dll!0x195FC85
  libcef.dll!0x14A99AA
  libcef.dll!0x4FC1B8
  libcef.dll!0x4FC208
  libcef.dll!0x195F1D3
  libcef.dll!0x195EFFE
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FDDC0
  ```

- 1 ms (8.3 %) `(kernel only)`

### Thread 5176 wait sites

- 105 switches (97.2 %)

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

- 1 switches (0.9 %)

  ```text
  libcef.dll!0x35073C3
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

- 1 switches (0.9 %)

  ```text
  libcef.dll!0x2E4CFB0
  libcef.dll!0x362D4E5
  libcef.dll!0x362AD93
  libcef.dll!0x362B571
  libcef.dll!0x36330A1
  libcef.dll!0x3506ED2
  libcef.dll!0x362EDF1
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  ```

### Thread 16364 CrGpuMain: 11 ms, 19 switches

### Thread 16364 hottest stacks

- 6 ms (54.5 %)

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

- 4 ms (36.4 %) `(no stack)`
- 1 ms (9.1 %)

  ```text
  libcef.dll!0x77CC680
  libcef.dll!0x3676CFF
  libcef.dll!0x36774E1
  libcef.dll!0x153FCB6
  libcef.dll!0x24EB48B
  libcef.dll!0x4FF362
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  libcef.dll!0x135832D
  ```

### Thread 16364 wait sites

- 13 switches (68.4 %)

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

- 3 switches (15.8 %)

  ```text
  ntdll.dll!ZwWaitForAlertByThreadId
  ntdll.dll!RtlSleepConditionVariableSRW
  KernelBase.dll!SleepConditionVariableSRW
  igd10umt64xe.dll!0xABC0A
  igd10umt64xe.dll!0xB9E7B
  igd10umt64xe.dll!0x6ED90
  igd10umt64xe.dll!0x6620E
  d3d11.dll!NDXGI::CDevice::Flush
  d3d11.dll!NDXGI::CDevice::FlushAndEnqueueSetEvent
  d3d11.dll!CContext::TID3D11DeviceContext_Flush_AppEntered
  libGLESv2.dll!0x2EFFCB
  libcef.dll!0x24EB511
  ```

- 1 switches (5.3 %)

  ```text
  libcef.dll!0x1FCB110
  libcef.dll!0x19C7193
  libcef.dll!0x153FD14
  libcef.dll!0x24EB48B
  libcef.dll!0x4FF362
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x18696ED
  libcef.dll!0x1357603
  ```

- 1 switches (5.3 %) `(kernel only)`

### Thread 2568 Chrome_DevToolsHandlerThread: 9 ms, 51 switches

### Thread 2568 hottest stacks

- 2 ms (22.2 %)

  ```text
  ntdll.dll!ZwDeviceIoControlFile
  mswsock.dll!WSPSend
  ws2_32.dll!WSASend
  libcef.dll!0x196254B
  libcef.dll!0x19639DA
  libcef.dll!0x1760A37
  libcef.dll!0x1BF8DED
  libcef.dll!0x1760839
  libcef.dll!0xE7672D
  libcef.dll!0xE7C785
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  ```

- 2 ms (22.2 %) `(no stack)`
- 1 ms (11.1 %)

  ```text
  libcef.dll!0x35229E2
  libcef.dll!0x362ED9B
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (11.1 %)

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

### Thread 2568 wait sites

- 37 switches (72.5 %)

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

- 8 switches (15.7 %)

  ```text
  win32u.dll!ZwUserPostMessage
  user32.dll!PostMessageW
  libcef.dll!0x34FD534
  libcef.dll!0x362E701
  libcef.dll!0x36282FE
  libcef.dll!0x3505D6B
  libcef.dll!0xE7A038
  libcef.dll!0x1761363
  libcef.dll!0x176117E
  libcef.dll!0x3655E77
  libcef.dll!0x3655BB0
  libcef.dll!0x365576E
  ```

- 2 switches (3.9 %)

  ```text
  mswsock.dll!WSPEnumNetworkEvents
  ws2_32.dll!WSAEnumNetworkEvents
  libcef.dll!0x36556ED
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x34FDDC0
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  ```

- 1 switches (2.0 %)

  ```text
  ntdll.dll!NtAssociateWaitCompletionPacket
  ntdll.dll!TpSetWaitEx
  ntdll.dll!RtlRegisterWait
  kernel32.dll!RegisterWaitForSingleObject
  libcef.dll!0x34FBE54
  libcef.dll!0x3655994
  libcef.dll!0x3655D7A
  libcef.dll!0x17610C9
  libcef.dll!0x3655E77
  libcef.dll!0x3655BB0
  libcef.dll!0x365576E
  libcef.dll!0x350A82D
  ```

### Thread 220 ThreadPoolForegroundWorker: 6 ms, 33 switches

### Thread 220 hottest stacks

- 1 ms (16.7 %)

  ```text
  ntdll.dll!ZwClose
  KernelBase.dll!CloseHandle
  powrprof.dll!CallNtPowerInformation
  libcef.dll!0x190F960
  libcef.dll!0x190F818
  libcef.dll!0x13E8A30
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  libcef.dll!0x39AB330
  libcef.dll!0x25D44D8
  libcef.dll!0x13CC62F
  ```

- 1 ms (16.7 %)

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

- 1 ms (16.7 %)

  ```text
  ntdll.dll!ZwOpenThreadToken
  KernelBase.dll!OpenThreadToken
  powrprof.dll!CallNtPowerInformation
  libcef.dll!0x190F960
  libcef.dll!0x190F818
  libcef.dll!0x13E8A30
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  libcef.dll!0x39AB330
  libcef.dll!0x25D44D8
  libcef.dll!0x13CC62F
  ```

- 1 ms (16.7 %) `(no stack)`

### Thread 220 wait sites

- 30 switches (90.9 %)

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

- 2 switches (6.1 %)

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

- 1 switches (3.0 %)

  ```text
  ntdll.dll!ZwDuplicateToken
  ntdll.dll!RtlImpersonateSelfEx
  KernelBase.dll!ImpersonateSelf
  powrprof.dll!CallNtPowerInformation
  libcef.dll!0x190F960
  libcef.dll!0x190F818
  libcef.dll!0x13E8A30
  libcef.dll!0x1907642
  libcef.dll!0x1907727
  libcef.dll!0x350A82D
  libcef.dll!0x37C1726
  libcef.dll!0x37C1089
  ```

### Thread 7568 ThreadPoolServiceThread: 5 ms, 75 switches

### Thread 7568 hottest stacks

- 1 ms (20.0 %)

  ```text
  libcef.dll!0x25D4710
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
  libcef.dll!0x13EA894
  libcef.dll!0x1EEE3B8
  libcef.dll!0x13EAA32
  ```

- 1 ms (20.0 %) `(kernel only)`
- 1 ms (20.0 %) `(no stack)`
- 1 ms (20.0 %)

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

### Thread 7568 wait sites

- 69 switches (92.0 %)

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

- 2 switches (2.7 %)

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

- 2 switches (2.7 %)

  ```text
  libcef.dll!0x362EC69
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

- 1 switches (1.3 %)

  ```text
  libcef.dll!0x922BA89
  libcef.dll!0x1EF3580
  libcef.dll!0x37C6A39
  libcef.dll!0x1EF3365
  libcef.dll!0x1907196
  libcef.dll!0x1907346
  libcef.dll!0x1EEE6E7
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  libcef.dll!0x3637B80
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  ```

### Thread 9592 Chrome_ChildIOThread: 4 ms, 59 switches

### Thread 9592 hottest stacks

- 1 ms (25.0 %) `(no stack)`
- 1 ms (25.0 %)

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
  libcef.dll!0x350A82D
  libcef.dll!0x362F063
  ```

- 1 ms (25.0 %)

  ```text
  kernel32.dll!GetQueuedCompletionStatusStub
  libcef.dll!0x34FDE1F
  libcef.dll!0x13D281E
  libcef.dll!0x190C0A6
  libcef.dll!0x13FD35F
  libcef.dll!0x13EA894
  libcef.dll!0x183466B
  libcef.dll!0x13EAA32
  libcef.dll!0x13CC62F
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (25.0 %)

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
  ntdll.dll!RtlUserThreadStart
  ```

### Thread 9592 wait sites

- 57 switches (96.6 %)

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

- 2 switches (3.4 %)

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

## RTSS.exe

### Threads

|  PID |   TID | Name | CPU ms | Switches | Switches/s |
| ---: | ----: | ---- | -----: | -------: | ---------: |
| 5988 |  5992 |      |     29 |      305 |          6 |
| 5988 |  9792 |      |      1 |       56 |          1 |
| 5988 | 13644 |      |      0 |        9 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 30 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 12 ms (40.0 %) `?`
- 2 ms (6.7 %) `mfc140.dll!CMapPtrToPtr::GetValueAt`
- 2 ms (6.7 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 1 ms (3.3 %) `OverlayEditor.dll!0x3ED90`
- 1 ms (3.3 %) `ntdll.dll!RtlAbPreAcquire`
- 1 ms (3.3 %) `win32kfull.sys!_FindWindowEx`
- 1 ms (3.3 %) `OverlayEditor.dll!0x39130`
- 1 ms (3.3 %) `ntoskrnl.exe!EtwpTraceStackWalk`
- 1 ms (3.3 %) `ntoskrnl.exe!ObpLookupObjectName`
- 1 ms (3.3 %) `mfc140.dll!AfxInternalPumpMessage`
- 1 ms (3.3 %) `ntoskrnl.exe!KxReleaseQueuedSpinLock`
- 1 ms (3.3 %) `mfc140.dll!CWinThread::PreTranslateMessage`
- 1 ms (3.3 %) `ntoskrnl.exe!MiResolveProtoPteFault`
- 1 ms (3.3 %) `RTSS.exe!0x16770`
- 1 ms (3.3 %) `OverlayEditor.dll!0x34AF0`
- 1 ms (3.3 %) `win32kfull.sys!xxxRealSleepThread`
- 1 ms (3.3 %) `ntoskrnl.exe!ExAllocateHeapPool`

### Hottest sampled stacks (leaf first)

- 12 ms (40.0 %) `(no stack)`
- 2 ms (6.7 %)

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

- 1 ms (3.3 %)

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

- 1 ms (3.3 %)

  ```text
  OverlayEditor.dll!0x3ED90
  OverlayEditor.dll!0x3B703
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
  user32.dll!DispatchMessageA
  ```

- 1 ms (3.3 %)

  ```text
  ntdll.dll!RtlAbPreAcquire
  mfc140.dll!CThreadSlotData::GetThreadValue
  mfc140.dll!CThreadLocalObject::GetData
  mfc140.dll!CThreadLocal<_AFX_THREAD_STATE>::GetData
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (3.3 %)

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
  user32.dll!InternalFindWindowExA
  RTSS.exe!0x16C71
  ```

- 1 ms (3.3 %)

  ```text
  mfc140.dll!CMapPtrToPtr::GetValueAt
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

- 1 ms (3.3 %)

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
  user32.dll!PostMessageA
  OverlayEditor.dll!0x30DD5
  ```

- 1 ms (3.3 %)

  ```text
  OverlayEditor.dll!0x39130
  OverlayEditor.dll!0x34574
  OverlayEditor.dll!0x36BDE
  OverlayEditor.dll!0x4B867
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

- 1 ms (3.3 %)

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

- 1 ms (3.3 %)

  ```text
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (3.3 %)

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
  user32.dll!InternalFindWindowExA
  RTSS.exe!0x16C9D
  ```

- 1 ms (3.3 %)

  ```text
  mfc140.dll!CWinThread::PreTranslateMessage
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 1 ms (3.3 %)

  ```text
  OverlayEditor.dll!0x3B29D
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
  user32.dll!DispatchMessageA
  mfc140.dll!AfxInternalPumpMessage
  ```

- 1 ms (3.3 %)

  ```text
  RTSS.exe!0x16770
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
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ```

- 1 ms (3.3 %)

  ```text
  OverlayEditor.dll!0x34AF0
  OverlayEditor.dll!0x36BC1
  OverlayEditor.dll!0x4B867
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
  mfc140.dll!AfxWinMain
  ```

- 1 ms (3.3 %)

  ```text
  wow64.dll!Wow64ShallowThunkAllocObjectAttributes32TO64_FNC
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
  ```

- 1 ms (3.3 %)

  ```text
  ntdll.dll!NtMapViewOfSection
  wow64.dll!whNtMapViewOfSection
  wow64.dll!Wow64SystemServiceEx
  wow64cpu.dll!ServiceNoTurbo
  wow64cpu.dll!BTCpuSimulate
  wow64.dll!RunCpuSimulation
  wow64.dll!Wow64LdrpInitialize
  ntdll.dll!LdrpInitializeProcess
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ntdll.dll!NtMapViewOfSection
  KernelBase.dll!MapViewOfFile
  OverlayEditor.dll!0x3B28F
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 192 switches (51.9 %)

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

- 40 switches (10.8 %)

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

- 30 switches (8.1 %)

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

- 25 switches (6.8 %)

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

- 25 switches (6.8 %)

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

- 3 switches (0.8 %)

  ```text
  ntdll.dll!_memcmp
  ntdll.dll!EtwpRegistrationCompare
  ntdll.dll!EtwpFindRegistration
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWaitCompletion
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (0.5 %)

  ```text
  ntdll.dll!NtQueryObject
  wow64.dll!Wow64ShallowThunkAllocObjectAttributes32TO64_FNC
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
  ```

- 2 switches (0.5 %)

  ```text
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
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  ```

- 2 switches (0.5 %)

  ```text
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
  RTSS.exe!0x46692
  ```

- 2 switches (0.5 %)

  ```text
  KernelBase.dll!GetEightBitStringToUnicodeStringRoutine
  kernel32.dll!OpenFileMappingA
  OverlayEditor.dll!0x3B26D
  OverlayEditor.dll!0x4A3F1
  OverlayEditor.dll!0x36BAA
  OverlayEditor.dll!0x4B867
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  ```

- 2 switches (0.5 %)

  ```text
  user32.dll!_EndUserApiHook
  user32.dll!CallWindowProcAorW
  user32.dll!CallWindowProcA
  mfc140.dll!CWnd::DefWindowProcA
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!SendMessageWorker
  user32.dll!SendMessageInternal
  ```

- 2 switches (0.5 %)

  ```text
  mfc140.dll!ATL::operator==
  mfc140.dll!CStringList::Find
  OverlayEditor.dll!0x5492B
  OverlayEditor.dll!0x551CC
  OverlayEditor.dll!0x36BC1
  OverlayEditor.dll!0x4B867
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  ```

- 2 switches (0.5 %)

  ```text
  ntdll.dll!_memcpy
  ntdll.dll!LdrpAllocateTls
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (0.5 %)

  ```text
  OverlayEditor.dll!0x5D72D
  OverlayEditor.dll!0x36BFD
  OverlayEditor.dll!0x4B867
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  user32.dll!DispatchMessageA
  ```

- 2 switches (0.5 %)

  ```text
  mfc140.dll!AfxGetModuleThreadState
  mfc140.dll!AfxInternalPumpMessage
  mfc140.dll!AfxWinMain
  RTSS.exe!0x46692
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (0.5 %)

  ```text
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 1 switches (0.3 %)

  ```text
  mfc140.dll!CWnd::Default
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
  mfc140.dll!AfxWinMain
  ```

- 1 switches (0.3 %)

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
  ```

- 1 switches (0.3 %)

  ```text
  RTSS.exe!0x744D
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

- 1 switches (0.3 %)

  ```text
  ntdll.dll!RtlMultiByteToUnicodeN
  user32.dll!InternalFindWindowExA
  RTSS.exe!0x16C9D
  RTSS.exe!0x1A500
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!_InternalCallWinProc
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  ```

### Thread 5992 : 29 ms, 305 switches

### Thread 5992 hottest stacks

- 12 ms (41.4 %) `(no stack)`
- 2 ms (6.9 %)

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

- 1 ms (3.4 %)

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

- 1 ms (3.4 %)

  ```text
  OverlayEditor.dll!0x3ED90
  OverlayEditor.dll!0x3B703
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
  user32.dll!DispatchMessageA
  ```

### Thread 5992 wait sites

- 192 switches (63.0 %)

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

- 40 switches (13.1 %)

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

- 25 switches (8.2 %)

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

- 2 switches (0.7 %)

  ```text
  ntdll.dll!NtQueryObject
  wow64.dll!Wow64ShallowThunkAllocObjectAttributes32TO64_FNC
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
  ```

### Thread 9792 : 1 ms, 56 switches

### Thread 9792 hottest stacks

- 1 ms (100.0 %)

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
  user32.dll!PostMessageA
  OverlayEditor.dll!0x30DD5
  ```

### Thread 9792 wait sites

- 30 switches (53.6 %)

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

- 25 switches (44.6 %)

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

- 1 switches (1.8 %)

  ```text
  OverlayEditor.dll!0x30DB9
  winmm.dll!timeThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

### Thread 13644 : 0 ms, 9 switches

### Thread 13644 wait sites

- 3 switches (33.3 %)

  ```text
  ntdll.dll!_memcmp
  ntdll.dll!EtwpRegistrationCompare
  ntdll.dll!EtwpFindRegistration
  ntdll.dll!EtwDeliverDataBlock
  ntdll.dll!EtwpNotificationThread
  ntdll.dll!TppExecuteWaitCallback
  ntdll.dll!TppWaitCompletion
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!__RtlUserThreadStart
  ntdll.dll!_RtlUserThreadStart
  ```

- 2 switches (22.2 %)

  ```text
  ntdll.dll!_memcpy
  ntdll.dll!LdrpAllocateTls
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 2 switches (22.2 %)

  ```text
  ntdll.dll!LdrpInitializeThread
  ntdll.dll!_LdrpInitialize
  ntdll.dll!LdrpInitializeInternal
  ntdll.dll!LdrInitializeThunk
  ```

- 1 switches (11.1 %) `ntdll.dll!RtlUserThreadStart`

## RTSSHooksLoader64.exe

### Threads

|  PID |   TID | Name | CPU ms | Switches | Switches/s |
| ---: | ----: | ---- | -----: | -------: | ---------: |
| 1308 | 15304 |      |      0 |        1 |          0 |

### Wakeups by wait site (switch-in stack, leaf first)

- 1 switches (100.0 %) `ntdll.dll!RtlUserThreadStart`

### Thread 15304 : 0 ms, 1 switches

### Thread 15304 wait sites

- 1 switches (100.0 %) `ntdll.dll!RtlUserThreadStart`

## LHMDataProvider.exe

### Threads

|  PID |   TID | Name | CPU ms | Switches | Switches/s |
| ---: | ----: | ---- | -----: | -------: | ---------: |
| 9776 | 13048 |      |    129 |     1328 |         24 |
| 9776 | 11888 |      |     13 |        6 |          0 |

### CPU by WSGM frame (inclusive, first own frame from the leaf)

- 142 ms (100.0 %) `(no WSGM frame)`

### CPU by leaf function

- 67 ms (47.2 %) `?`
- 8 ms (5.6 %) `ntoskrnl.exe!RtlpLookupFunctionEntryForStackWalks`
- 2 ms (1.4 %) `ntoskrnl.exe!RtlpxVirtualUnwind`
- 2 ms (1.4 %) `clr.dll!StubHelpers::ValueClassMarshaler__ConvertToManaged`
- 2 ms (1.4 %) `LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Sensor::set_Value 0x0`
- 2 ms (1.4 %) `ntoskrnl.exe!ExAllocateHeapPool`
- 2 ms (1.4 %) `ntoskrnl.exe!RtlpLookupUserFunctionTableInverted`
- 1 ms (0.7 %) `ntoskrnl.exe!__memset_spec_ermsb`
- 1 ms (0.7 %) `clr.dll!FmtValueTypeUpdateNative`
- 1 ms (0.7 %) `LHMDataProvider.sys!0x10D8`
- 1 ms (0.7 %) `clr.dll!JIT_InitPInvokeFrame`
- 1 ms (0.7 %) `ucx01000.sys!RootHub_Pdo_EvtInternalDeviceControlIrpPreprocessCallback`
- 1 ms (0.7 %) `mfc140.dll!CWinApp::OnIdle`
- 1 ms (0.7 %) `clr.dll!LayoutUpdateNative`
- 1 ms (0.7 %) `clr.dll!GetThread`
- 1 ms (0.7 %) `ucrtbase.dll!strcpy_s`
- 1 ms (0.7 %) `Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0`
- 1 ms (0.7 %) `vcruntime140_clr0400.dll!memcpy_repmovs`
- 1 ms (0.7 %) `ntoskrnl.exe!KiDeliverApc`
- 1 ms (0.7 %) `ntoskrnl.exe!SwapContext`

### Hottest sampled stacks (leaf first)

- 67 ms (47.2 %) `(no stack)`
- 10 ms (7.0 %)

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

- 8 ms (5.6 %)

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

- 8 ms (5.6 %)

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

- 4 ms (2.8 %) `(kernel only)`
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
  clr.dll!StubHelpers::ValueClassMarshaler__ConvertToManaged
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
  LHMDataProvider.exe!0x1A24
  ```

- 2 ms (1.4 %)

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
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  ```

- 2 ms (1.4 %)

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

- 2 ms (1.4 %)

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
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  ```

- 2 ms (1.4 %)

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

- 1 ms (0.7 %)

  ```text
  clr.dll!FmtValueTypeUpdateNative
  clr.dll!StubHelpers::ValueClassMarshaler__ConvertToNative
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
  Importer.dll!dynamicClass::IL_STUB_ReversePInvoke 0x0
  clr.dll!UMThunkStub
  ```

- 1 ms (0.7 %)

  ```text
  clr.dll!JIT_InitPInvokeFrame
  System.ni.dll!0x352F42
  System.ni.dll!0x34D1B3
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.Cpu.GenericCpu::Update 0x0
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

- 1 ms (0.7 %)

  ```text
  mfc140.dll!CWinApp::OnIdle
  mfc140.dll!CWinThread::Run
  mfc140.dll!AfxWinMain
  LHMDataProvider.exe!0x222E
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 ms (0.7 %)

  ```text
  clr.dll!LayoutUpdateNative
  clr.dll!FmtValueTypeUpdateNative
  clr.dll!StubHelpers::ValueClassMarshaler__ConvertToNative
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
  ```

- 1 ms (0.7 %)

  ```text
  clr.dll!GetThread
  clr.dll!ThreadStateHolder::~ThreadStateHolder
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
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  ```

- 1 ms (0.7 %)

  ```text
  ucrtbase.dll!strcpy_s
  LHMDataProvider.exe!0x1262
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
  mfc140.dll!AfxWinMain
  LHMDataProvider.exe!0x222E
  ```

- 1 ms (0.7 %)

  ```text
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
  LHMDataProvider.exe!0x11BF
  LHMDataProvider.exe!0x1A24
  ```

### Wakeups by wait site (switch-in stack, leaf first)

- 690 switches (51.7 %)

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

- 298 switches (22.3 %)

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

- 134 switches (10.0 %)

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

- 63 switches (4.7 %)

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

- 30 switches (2.2 %)

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

- 24 switches (1.8 %)

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

- 6 switches (0.4 %)

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

- 5 switches (0.4 %)

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

- 5 switches (0.4 %)

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
  ```

- 5 switches (0.4 %)

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
  ```

- 3 switches (0.2 %) `ntdll.dll!RtlUserThreadStart`
- 3 switches (0.2 %)

  ```text
  win32u.dll!ZwGdiDdDDIQueryAdapterInfo
  Importer.dll!dynamicClass::IL_STUB_PInvoke 0x0
  LibreHardwareMonitorLib.dll!LibreHardwareMonitor.Hardware.D3DDisplayDevice::GetNodeMetaData 0x0
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

- 3 switches (0.2 %)

  ```text
  clr.dll!JIT_InitPInvokeFrame
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
  ```

- 2 switches (0.1 %)

  ```text
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
  clr.dll!UMThunkStub
  LHMDataProvider.exe!0x11BF
  ```

- 2 switches (0.1 %)

  ```text
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

- 2 switches (0.1 %)

  ```text
  win32u.dll!ZwUserPeekMessage
  user32.dll!_PeekMessage
  user32.dll!PeekMessageA
  LHMDataProvider.exe!0x1A50
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!UserCallWinProcCheckWow
  user32.dll!DispatchMessageWorker
  mfc140.dll!AfxInternalPumpMessage
  ```

- 2 switches (0.1 %)

  ```text
  ?!0x0
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
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 2 switches (0.1 %)

  ```text
  ntdll.dll!RtlInitializeExtendedContext2
  ntdll.dll!RtlRaiseException
  KernelBase.dll!RaiseException
  KernelBase.dll!OutputDebugStringA
  LHMDataProvider.exe!0x12AF
  LHMDataProvider.exe!0x1A24
  mfc140.dll!CWnd::OnWndMsg
  mfc140.dll!CWnd::WindowProc
  mfc140.dll!AfxCallWndProc
  mfc140.dll!AfxWndProc
  mfc140.dll!AfxWndProcBase
  user32.dll!UserCallWinProcCheckWow
  ```

### Thread 13048 : 129 ms, 1328 switches

### Thread 13048 hottest stacks

- 54 ms (41.9 %) `(no stack)`
- 10 ms (7.7 %)

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

- 8 ms (6.2 %)

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

- 8 ms (6.2 %)

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

- 690 switches (52.0 %)

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

- 298 switches (22.4 %)

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

- 134 switches (10.1 %)

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

- 63 switches (4.7 %)

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

### Thread 11888 : 13 ms, 6 switches

### Thread 11888 hottest stacks

- 13 ms (100.0 %) `(no stack)`

### Thread 11888 wait sites

- 3 switches (50.0 %) `ntdll.dll!RtlUserThreadStart`
- 2 switches (33.3 %)

  ```text
  ntdll.dll!NtWaitForWorkViaWorkerFactory
  ntdll.dll!TppWorkerThread
  kernel32.dll!BaseThreadInitThunk
  ntdll.dll!RtlUserThreadStart
  ```

- 1 switches (16.7 %)

  ```text
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
  ```
