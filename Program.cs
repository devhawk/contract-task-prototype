using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Neo.VM;
using ExecutionContext = Neo.VM.ExecutionContext;
using StackItem = Neo.VM.Types.StackItem;
using StackItemType = Neo.VM.Types.StackItemType;

/*

My idea is to use the C# async/await infrastructure for scenarios where a native contract invokes a
VM contract.

This problem seems very similar to using async/await to avoid blocking the UI thread of a client app.
In most UI frameworks, there is a single dedicated thread for servicing the UI components. If you block
this thread - say with a long running IO operation - the UI becomes unresponsive. With async/await,
these long running operations return a Task/Task<T> and are awaited by code running on the UI thread.
When the UI thread encounters the awaited Task, it exits the method that started the long running
operation and continues processing UI events. When the long running operation completes, the remainder
of the async method where the await occurred is scheduled to be executed on the UI thread.

We see something similar with Native contracts that invoke CallFromNativeContract. Calling StepOut in a
loop until we return to the execution context where the native contract was invoked is similar to 
blocking the UI thread on a long running operation. In the UI thread scenario, we want the UI thread to 
return to the UI processing loop to keep the UI responsive until the task completes. In the Neo VM scenario, 
we want the execution thread to return to the debugger loop so the user can control program execution. 

So can we use C#'s async/await infrastructure here? Yes, we can. C# 7 introduced new infrastructure to 
enable developers to build "task-like types" so that async/await can be used with types other than the 
built in Task/Task<T> types. More details on task like types is available at:
https://github.com/dotnet/roslyn/blob/master/docs/features/task-types.md

The sample code below is a stripped down example using NeoVM (but not neo.dll). In this example, 
CallFromNativeContract returns a "ContractTask" type, a "task-like type" that can be awaited, but that
doesn't spin up threads from the thread pool to complete it's operations. Rather, the ContractTask gets
associated with the executionContext associated with the VM script being called. When that context is
unloaded, the remaining code associated with that ContractTask is executed.
*/

namespace await_test
{
    [AsyncMethodBuilder(typeof(ContractTaskBuilder))]
    class ContractTask
    {
        Action? continuation = null;

        public ContractTaskAwaiter GetAwaiter() => new ContractTaskAwaiter(this);
        public bool IsCompleted { get; private set; } = false;
        public Exception? Exception { get; private set; }

        public void OnCompleted(Action continuation)
        {
            var value = Interlocked.CompareExchange<Action?>(ref this.continuation, continuation, null);
            if (value != null) throw new InvalidOperationException();
        }

        public void SetException(Exception exception)
        {
            if (IsCompleted) throw new InvalidOperationException();
            Exception = exception;
        }

        public virtual void SetResult()
        {
            RunContinuation();
        }

        public virtual void SetResult(StackItem item)
        {
            if (!item.IsNull) throw new InvalidOperationException();
            RunContinuation();
        }

        public void RunContinuation()
        {
            if (IsCompleted
                || continuation == null 
                || Exception != null)
            {
                throw new InvalidOperationException();
            }

            IsCompleted = true;
            continuation();
        }
    }

    [AsyncMethodBuilder(typeof(ContractTaskBuilder<>))]
    class ContractTask<T> : ContractTask
    {
        public T? Result { get; private set; }

        public new ContractTaskAwaiter<T> GetAwaiter() => new ContractTaskAwaiter<T>(this);

        public void SetResult(T item)
        {
            Result = item;
            RunContinuation();
        }

        public override void SetResult(StackItem item)
        {
            if (typeof(T) == typeof(string))
            {
                SetResult((T)(object)item.GetString());
            }
            else if (typeof(T) == typeof(long))
            {
                SetResult((T)(object)(long)item.GetInteger());
            }
            else
            {
                throw new InvalidOperationException();
            }
        }
    }

    struct ContractTaskAwaiter : INotifyCompletion
    {
        private readonly ContractTask contractTask;

        public ContractTaskAwaiter(ContractTask contractTask)
        {
            this.contractTask = contractTask;
        }

        public bool IsCompleted => contractTask.IsCompleted;
        public void GetResult() { if (!IsCompleted) throw new InvalidOperationException(); }
        public void OnCompleted(Action continuation) => contractTask.OnCompleted(continuation);
    }

    struct ContractTaskAwaiter<T> : INotifyCompletion
    {
        private readonly ContractTask<T> contractTask;

        public ContractTaskAwaiter(ContractTask<T> contractTask)
        {
            this.contractTask = contractTask;
        }

        public bool IsCompleted => contractTask.IsCompleted;
        public T? GetResult() => IsCompleted ? contractTask.Result : throw new InvalidOperationException();
        public void OnCompleted(Action continuation) => contractTask.OnCompleted(continuation);
    }

    struct ContractTaskBuilder
    {
        ContractTask task;
        public ContractTask Task => task ??= new ContractTask();

        public static ContractTaskBuilder Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            if (stateMachine == null) throw new ArgumentNullException(nameof(stateMachine));
            stateMachine.MoveNext();
        }

        public void SetException(Exception exception) => task.SetException(exception);
        public void SetResult() => task.SetResult();
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // need to use ContractTaskStateMachineBox here vecause stateMachine is a ref parameter
            var box = new ContractTaskStateMachineBox<TStateMachine>(stateMachine);
            awaiter.OnCompleted(box.MoveNextAction);
        }
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // need to use ContractTaskStateMachineBox here vecause stateMachine is a ref parameter
            var box = new ContractTaskStateMachineBox<TStateMachine>(stateMachine);
            awaiter.OnCompleted(box.MoveNextAction);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
    }

    struct ContractTaskBuilder<T>
    {
        ContractTask<T> task;
        public ContractTask<T> Task => task ??= new ContractTask<T>();

        public static ContractTaskBuilder<T> Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            if (stateMachine == null) throw new ArgumentNullException(nameof(stateMachine));
            stateMachine.MoveNext();
        }

        public void SetException(Exception exception) => task.SetException(exception);
        public void SetResult(T result) => task.SetResult(result);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // need to use ContractTaskStateMachineBox here vecause stateMachine is a ref parameter
            var box = new ContractTaskStateMachineBox<TStateMachine>(stateMachine);
            awaiter.OnCompleted(box.MoveNextAction);
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // need to use ContractTaskStateMachineBox here vecause stateMachine is a ref parameter
            var box = new ContractTaskStateMachineBox<TStateMachine>(stateMachine);
            awaiter.OnCompleted(box.MoveNextAction);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
    }

    class ContractTaskStateMachineBox<TStateMachine> where TStateMachine : IAsyncStateMachine
    {
        public readonly TStateMachine StateMachine;
        private Action? _moveNextAction;
        public Action MoveNextAction => _moveNextAction ??= new Action(MoveNext);

        public ContractTaskStateMachineBox(TStateMachine stateMachine)
        {
            StateMachine = stateMachine;
        }

        private void MoveNext()
        {
            if (StateMachine == null) throw new InvalidOperationException();
            StateMachine.MoveNext();
        }
    }

    // Stripped down ApplicationEngine for test purposes
    class AppEngine : ExecutionEngine
    {
        public Exception? FaultException { get; private set; }

        protected override void OnFault(Exception e)
        {
            Konsole.WriteLine($"OnFault: {e.Message}", ConsoleColor.Red);
            FaultException = e;
            base.OnFault(e);
        }

        protected override async void OnSysCall(uint method)
        {
            // Since OnSysCall is async void, exceptions need to be caught and handled locally. Any exception
            // thrown from an async void method gets swallowed.
            try
            {
                // SysCall 0 pops a single parameter from the eval stack, converts it to a string and prints it to the console
                if (method == 0)
                {
                    var arg = Pop().GetString();
                    Konsole.WriteLine($"SysCallPrint: \"{arg}\"", ConsoleColor.Yellow);
                    return;
                }

                // SysCalls 1-8 each match a sample sys call method to be invoked via reflection
                // SysCalls 6-8 all throw exceptions of various sorts for test purposes 
                // for the purposes of this prototype, none of the sys call methods take a parameter
                var methodInfo = method switch
                {
                    1 => GetMethod(nameof(NativeVoidReturn)),
                    2 => GetMethod(nameof(NativeLongReturn)),
                    3 => GetMethod(nameof(NativeAsyncVoidReturn)),
                    4 => GetMethod(nameof(NativeAsyncContractTaskReturn)),
                    5 => GetMethod(nameof(NativeAsyncTaskOfLongReturn)),
                    6 => GetMethod(nameof(NativeVoidReturnThrows)),
                    7 => GetMethod(nameof(NativeAsyncVoidReturnThrows)),
                    8 => GetMethod(nameof(NativeAsyncContractTaskReturnThrows)),
                    _ => throw new InvalidOperationException(),
                };

                static MethodInfo GetMethod(string name)
                {
                    const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    return typeof(AppEngine).GetMethod(name, bindingFlags) ?? throw new InvalidOperationException();
                }

                // invoke the selected method via reflection
                var result = methodInfo.Invoke(this, Array.Empty<object>());

                // For contractTask types, await the result
                var returnType = methodInfo.ReturnType;
                if (returnType.IsAssignableTo(typeof(ContractTask)))
                {
                    if (result == null) throw new InvalidOperationException();
                    await (ContractTask)result;

                    // for ContractTask<T> results, retrieve the result via reflection,
                    // convert to a StackItem and push it on the Evaluation Stack
                    if (returnType.IsGenericType)
                    {
                        var prop = returnType.GetProperty("Result") ?? throw new InvalidOperationException();
                        var awaitedResult = prop.GetMethod!.Invoke(result, Array.Empty<object>()) ?? throw new InvalidOperationException();
                        Push(Convert(awaitedResult));
                    }
                }
                else if (methodInfo.ReturnType != typeof(void))
                {
                    // for non-void and non-ContractTask types, convert the result
                    // to a StackItem and push it onto the evaluation stack
                    if (result == null) throw new InvalidOperationException();
                    Push(Convert(result));
                }
                else
                {
                    // for void return methods, we're done
                    System.Diagnostics.Debug.Assert(returnType == typeof(void));
                }
            }
            catch (Exception ex)
            {
                OnFault(ex);
            }

            // helper method to convert object instances to StackItems
            static StackItem Convert(object obj) => obj switch
            {
                string s => s,
                long i => i,
                _ => throw new InvalidCastException()
            };
        }

        // sample OnSysCall method that returns void
        void NativeVoidReturn()
        {
            Konsole.WriteLine(nameof(NativeVoidReturn), ConsoleColor.Cyan);
        }

        // sample OnSysCall method that returns void and throws
        void NativeVoidReturnThrows()
        {
            throw new Exception(nameof(NativeVoidReturnThrows));
        }

        // sample OnSysCall method that returns a long
        long NativeLongReturn()
        {
            var now = DateTimeOffset.Now.Ticks;
            Konsole.WriteLine($"{nameof(NativeLongReturn)} {now}", ConsoleColor.Cyan);
            return now;
        }

        // sample OnSysCall async method that returns void
        async void NativeAsyncVoidReturn()
        {
            Konsole.WriteLine($"{nameof(NativeAsyncVoidReturn)} START", ConsoleColor.Green);
            await CallFromNativeContract();
            Konsole.WriteLine($"{nameof(NativeAsyncVoidReturn)} END", ConsoleColor.Magenta);
        }

        // sample OnSysCall async method that returns void and throws
        async void NativeAsyncVoidReturnThrows()
        {
            Konsole.WriteLine($"{nameof(NativeAsyncVoidReturnThrows)} START", ConsoleColor.Green);
            await CallFromNativeContract(true);
            Konsole.WriteLine($"{nameof(NativeAsyncVoidReturnThrows)} END", ConsoleColor.Magenta);
        }

        // sample OnSysCall async method that returns awaitable ContractTask
        async ContractTask NativeAsyncContractTaskReturn()
        {
            Konsole.WriteLine($"{nameof(NativeAsyncContractTaskReturn)} START", ConsoleColor.Green);
            await CallFromNativeContract();
            Konsole.WriteLine($"{nameof(NativeAsyncContractTaskReturn)} END", ConsoleColor.Magenta);
        }

        // sample OnSysCall async method that returns awaitable ContractTask
        async ContractTask NativeAsyncContractTaskReturnThrows()
        {
            Konsole.WriteLine($"{nameof(NativeAsyncContractTaskReturnThrows)} START", ConsoleColor.Green);
            await CallFromNativeContract(true);
            Konsole.WriteLine($"{nameof(NativeAsyncContractTaskReturnThrows)} END", ConsoleColor.Magenta);
        }


        // sample OnSysCall async method that returns awaitable ContractTask<long>
        async ContractTask<string> NativeAsyncTaskOfLongReturn()
        {
            var now = DateTimeOffset.Now.Ticks;
            Konsole.WriteLine($"{nameof(NativeAsyncTaskOfLongReturn)} START", ConsoleColor.Green);
            var result = await CallFromNativeContract(now);
            Konsole.WriteLine($"{nameof(NativeAsyncTaskOfLongReturn)} END {result}", ConsoleColor.Magenta);

            // do a simple math task and convert to string as a stand in for real business logic
            return $"{result - DateTimeOffset.UnixEpoch.Ticks}";
        }

        // store outstanding contractTasks waiting to be completed
        Dictionary<ExecutionContext, ContractTask> contractTasks = new Dictionary<ExecutionContext, ContractTask>();

        // Check to see if the unloaded context was associated with an outstanding ContractTask
        // if so, SetResult/Execption on the task as appropriate
        protected override void ContextUnloaded(ExecutionContext context)
        {
            base.ContextUnloaded(context);
            if (!contractTasks.Remove(context, out var task)) return;
            if (UncaughtException is not null)
            {
                // Note, this branch should never be taken. If VM code has an UncaughtException,
                // it will be thrown as a C# exception in ExecutionEngine.HandleException and 
                // ExecutionEngine.Execute will exit in the fault state w/o ever unloading
                // the relevant ExecutionContext.
                var ex = new VMUnhandledException(UncaughtException);
                task.SetException(ex);
            }
            else
            {
                var result = context.EvaluationStack.Count > 0 ? Pop() : StackItem.Null;
                task.SetResult(result);
            }
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 0 and returns.
        // optionally throws a VM exception
        ContractTask CallFromNativeContract(bool throws = false)
        {
            using var sb = new ScriptBuilder();
            sb.EmitPush($"{nameof(CallFromNativeContract)} void return");
            if (throws) sb.Emit(OpCode.THROW);
            sb.EmitSysCall(0);
            sb.Emit(OpCode.RET);

            var ctx = LoadScript(sb.ToArray(), 0);
            var task = new ContractTask();
            contractTasks.Add(ctx, task);
            return task;
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 0, 
        // pushes the long param value onto the VM execution stack and returns.
        // optionally throws a VM exception
        ContractTask<long> CallFromNativeContract(long value, bool throws = false)
        {
            using var sb = new ScriptBuilder();
            sb.EmitPush($"{nameof(CallFromNativeContract)} long return");
            if (throws) sb.Emit(OpCode.THROW);
            sb.EmitSysCall(0);
            sb.EmitPush(value);
            sb.Emit(OpCode.RET);

            var ctx = LoadScript(sb.ToArray(), 1);
            var task = new ContractTask<long>();
            contractTasks.Add(ctx, task);
            return task;
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 0, 
        // pushes the string param value onto the VM execution stack and returns.
        // optionally throws a VM exception

        ContractTask<string> CallFromNativeContract(string value, bool throws = false)
        {
            using var sb = new ScriptBuilder();
            sb.EmitPush($"{nameof(CallFromNativeContract)} string return");
            if (throws) sb.Emit(OpCode.THROW);
            sb.EmitSysCall(0);
            sb.EmitPush(value);
            sb.Emit(OpCode.RET);

            var ctx = LoadScript(sb.ToArray());
            var task = new ContractTask<string>();
            contractTasks.Add(ctx, task);
            return task;
        }

        // Emit information about each instruction before it is executed for debug purposes
        protected override void PreExecuteInstruction()
        {
            var instr = CurrentContext.CurrentInstruction;
            using var konsole = Konsole.Color(ConsoleColor.DarkGray);
            Console.Write($"{InvocationStack.Count} {CurrentContext.GetHashCode()} {CurrentContext.EvaluationStack.Count} {instr.OpCode}");
            switch (CurrentContext.CurrentInstruction.OpCode)
            {
                case Neo.VM.OpCode.SYSCALL:
                    Console.Write($" {instr.TokenU32}");
                    break;
                case Neo.VM.OpCode.PUSHDATA1:
                    Console.Write($" \"{Encoding.UTF8.GetString(instr.Operand.Span)}\"");
                    break;
                case Neo.VM.OpCode.PUSHINT64:
                    Console.Write($" {new BigInteger(instr.Operand.Span)}");
                    break;
            }

            if (CurrentContext.EvaluationStack.Count > 0)
            {
                var evalStack = CurrentContext.EvaluationStack.Select(ConvertItem);
                Console.Write($" ({string.Join(',', evalStack)})");
            }
            Console.WriteLine();

            static string ConvertItem(StackItem item)
            {
                return item.Type switch
                {
                    StackItemType.ByteString => $"\"{item.GetString()}\"",
                    StackItemType.Integer => item.GetInteger().ToString(),
                    _ => throw new InvalidOperationException()
                };
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            using var sb = new ScriptBuilder();
            sb.EmitSysCall(1);
            sb.EmitSysCall(2);
            sb.EmitSysCall(3);
            sb.EmitSysCall(4);
            sb.EmitSysCall(5);
            // SysCalls 6-8 all fault
            // sb.EmitSysCall(6);
            // sb.EmitSysCall(7);
            // sb.EmitSysCall(8);
            sb.Emit(OpCode.RET);

            var engine = new AppEngine();
            engine.LoadScript(sb.ToArray());
            engine.Execute();

            if (engine.State == VMState.FAULT)
            {
                Konsole.WriteLine($"Engine faulted: {engine.FaultException}", ConsoleColor.DarkRed);
            }
            else if (engine.ResultStack.Count > 0)
            {
                using var konsole = Konsole.Color(ConsoleColor.White);
                Console.WriteLine("\nResults:");

                foreach (var result in engine.ResultStack)
                {
                    var value = result.Type switch
                    {
                        StackItemType.Integer => result.GetInteger().ToString(),
                        StackItemType.ByteString => $"\"{result.GetString()}\"",
                        _ => throw new InvalidCastException()
                    };
                    Console.WriteLine($"  {value}");
                }
            }
        }
    }

    // Simple helper class to manage console colors
    class Konsole : IDisposable
    {
        ConsoleColor _orig_fg;

        [System.Diagnostics.DebuggerStepThrough]
        public static IDisposable Color(ConsoleColor fg)
        {
            var k = new Konsole()
            {
                _orig_fg = Console.ForegroundColor
            };

            Console.ForegroundColor = fg;

            return k;
        }

        [System.Diagnostics.DebuggerStepThrough]
        public void Dispose()
        {
            Console.ForegroundColor = _orig_fg;
        }

        // helper for writing a single line of text in a specified color
        public static void WriteLine(string text, ConsoleColor color)
        {
            using var konsole = Konsole.Color(color);
            Console.WriteLine(text);
        }
    }
}
