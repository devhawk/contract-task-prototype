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
built in Task/Task<T> types.

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

        private void OnParentCompleted(object? sender, EventArgs e)
        {
            RunContinuation();
        }

        public ContractTaskAwaiter GetAwaiter() => new ContractTaskAwaiter(this);
        public bool IsCompleted { get; private set; } = false;

        public void OnCompleted(Action continuation)
        {
            var value = Interlocked.CompareExchange<Action?>(ref this.continuation, continuation, null);
            if (value != null) throw new InvalidOperationException();
        }

        public void SetException(Exception exception)
        {
        }

        public virtual void SetResult()
        {
            RunContinuation();
        }

        public virtual void SetResult(StackItem item)
        {
            if (!item.IsNull) throw new InvalidOperationException();
        }

        public void RunContinuation()
        {
            if (continuation == null || IsCompleted)
            {
                throw new Exception();
            }

            continuation();
            IsCompleted = true;
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
        public void GetResult() { }
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
        public T? GetResult() => contractTask.Result;
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

        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
        public void SetException(Exception exception) => task.SetException(exception);
        public void SetResult() => task.SetResult();
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            var box = new ContractTaskStateMachineBox<TStateMachine>();
            box.StateMachine = stateMachine;
            awaiter.OnCompleted(box.MoveNextAction);
        }
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
    }

    class ContractTaskStateMachineBox<TStateMachine> where TStateMachine : IAsyncStateMachine
    {
        public TStateMachine? StateMachine;

        private Action? _moveNextAction;
        public Action MoveNextAction => _moveNextAction ??= new Action(MoveNext);

        private void MoveNext()
        {
            if (StateMachine == null) throw new InvalidOperationException();
            StateMachine.MoveNext();
        }
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

        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
        public void SetException(Exception exception) => task.SetException(exception);

        public void SetResult(T result) => task.SetResult(result);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            var box = new ContractTaskStateMachineBox<TStateMachine>();
            box.StateMachine = stateMachine;
            awaiter.OnCompleted(box.MoveNextAction);
        }
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            var box = new ContractTaskStateMachineBox<TStateMachine>();
            box.StateMachine = stateMachine;
            awaiter.OnCompleted(box.MoveNextAction);
        }
    }

    // Stripped down ApplicationEngine for test purposes
    class AppEngine : ExecutionEngine
    {
        protected override async void OnSysCall(uint method)
        {
            // SysCall 0 pops a single parameter from the eval stack, converts it to a string and prints it to the console
            if (method == 0)
            {
                var arg = Pop().GetString();
                using var konsole = Konsole.Color(ConsoleColor.Yellow);
                Console.WriteLine($"SysCallPrint: \"{arg}\"");
                return;
            }

            // SysCalls 1-4 each match a sample sys call method to be invoked via reflection
            // for the purposes of this prototype, none of the sys call methods take a parameter
            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methodInfo = method switch
            {
                1 => typeof(AppEngine).GetMethod(nameof(NativeVoidReturn), bindingFlags),
                2 => typeof(AppEngine).GetMethod(nameof(NativeLongReturn), bindingFlags),
                3 => typeof(AppEngine).GetMethod(nameof(NativeAsyncVoidReturn), bindingFlags),
                4 => typeof(AppEngine).GetMethod(nameof(NativeAsyncContractTaskReturn), bindingFlags),
                5 => typeof(AppEngine).GetMethod(nameof(NativeAsyncTaskOfLongReturn), bindingFlags),
                _ => throw new InvalidOperationException(),
            };

            if (methodInfo == null) throw new InvalidOperationException();
            var returnType = methodInfo.ReturnType;

            var result = methodInfo.Invoke(this, Array.Empty<object>());
            if (returnType.IsAssignableTo(typeof(ContractTask)))
            {
                if (result == null) throw new InvalidOperationException();
                await (ContractTask)result;

                if (returnType.IsGenericType)
                {
                    var prop = returnType.GetProperty("Result") ?? throw new InvalidOperationException();
                    var awaitedResult = prop.GetMethod!.Invoke(result, Array.Empty<object>());
                    var genericArgs = methodInfo.ReturnType.GetGenericArguments();
                    System.Diagnostics.Debug.Assert(genericArgs.Length == 1);
                    Push(Convert(genericArgs[0], awaitedResult!));
                }
            }

            else if (methodInfo.ReturnType != typeof(void))
            {
                if (result == null) throw new InvalidOperationException();
                Push(Convert(returnType, result));
            }
            else
            {
                System.Diagnostics.Debug.Assert(returnType == typeof(void));
            }

            static StackItem Convert(Type type, object obj)
            {
                if (type == typeof(long))
                {
                    return (long)obj;
                }

                if (type == typeof(string))
                {
                    return (string)obj;
                }

                throw new InvalidOperationException();
            }
        }

        // OnSysCall method that returns void
        void NativeVoidReturn()
        {
            using var konsole = Konsole.Color(ConsoleColor.Cyan);
            Console.WriteLine($"{nameof(NativeVoidReturn)}");
        }

        // OnSysCall method that returns a long
        long NativeLongReturn()
        {
            using var konsole = Konsole.Color(ConsoleColor.Cyan);
            var now = DateTimeOffset.Now.Ticks;
            Console.WriteLine($"{nameof(NativeLongReturn)} {now}");
            return now;
        }

        // OnSysCall async method that returns void
        async void NativeAsyncVoidReturn()
        {
            using (var konsole = Konsole.Color(ConsoleColor.Green))
            {
                Console.WriteLine($"{nameof(NativeAsyncVoidReturn)} START");
            }
            await CallFromNativeContract();
            using (var konsole = Konsole.Color(ConsoleColor.Magenta))
            {
                Console.WriteLine($"{nameof(NativeAsyncVoidReturn)} END");
            }
        }

        // OnSysCall async method that returns plain ContractTask
        async ContractTask NativeAsyncContractTaskReturn()
        {
            using (var konsole = Konsole.Color(ConsoleColor.Green))
            {
                Console.WriteLine($"{nameof(NativeAsyncContractTaskReturn)} START");
            }
            await CallFromNativeContract();
            using (var konsole = Konsole.Color(ConsoleColor.Magenta))
            {
                Console.WriteLine($"{nameof(NativeAsyncContractTaskReturn)} END");
            }
        }

        // OnSysCall async method that returns a ContractTask<long>
        async ContractTask<string> NativeAsyncTaskOfLongReturn()
        {
            var now = DateTimeOffset.Now.Ticks;
            using (var konsole = Konsole.Color(ConsoleColor.Green))
            {
                Console.WriteLine($"{nameof(NativeAsyncTaskOfLongReturn)} START");
            }
            var result = await CallFromNativeContract(now);
            using (var konsole = Konsole.Color(ConsoleColor.Magenta))
            {
                Console.WriteLine($"{nameof(NativeAsyncTaskOfLongReturn)} END {result}");
            }
            return $"{result - DateTimeOffset.UnixEpoch.Ticks}";
        }

        Dictionary<ExecutionContext, ContractTask> contractTasks = new Dictionary<ExecutionContext, ContractTask>();

        static T Convert<T>(StackItem item)
        {
            var type = typeof(T);
            if (type == typeof(long))
            {
                return (T)(object)(long)item.GetInteger();
            }

            if (type == typeof(string))
            {
                return (T)(object)item.GetString();
            }

            throw new InvalidOperationException();
        }

        private ContractTask RegisterCompletion(ExecutionContext context)
        {
            var task = new ContractTask();
            contractTasks.Add(context, task);
            return task;
        }
        
        private ContractTask<T> RegisterCompletion<T>(ExecutionContext context)
        {
            var task = new ContractTask<T>();
            contractTasks.Add(context, task);
            return task;
        }

        protected override void ContextUnloaded(ExecutionContext context)
        {
            base.ContextUnloaded(context);
            if (!contractTasks.Remove(context, out var task)) return;
            if (UncaughtException is not null) 
            {
                var ex = new VMUnhandledException(UncaughtException);
                task.SetException(ex);
            }
            else
            {
                var result = context.EvaluationStack.Count > 0 ? Pop() : StackItem.Null;
                task.SetResult(result);
            }
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 0 and returns
        ContractTask CallFromNativeContract()
        {
            using var sb = new ScriptBuilder();
            sb.EmitPush($"{nameof(CallFromNativeContract)} void return");
            sb.EmitSysCall(0);
            sb.Emit(OpCode.RET);

            var ctx = LoadScript(sb.ToArray(), 0);
            return RegisterCompletion(ctx);
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 0, 
        // pushes a long param value onto the VM execution stack and returns
        ContractTask<long> CallFromNativeContract(long value)
        {
            using var sb = new ScriptBuilder();
            sb.EmitPush($"{nameof(CallFromNativeContract)} long return");
            sb.EmitSysCall(0);
            sb.EmitPush(value);
            sb.Emit(OpCode.RET);

            var ctx = LoadScript(sb.ToArray(), 1);
            return RegisterCompletion<long>(ctx);
        }

        // // Fake CallFromNativeContract loads a script that invokes SysCall 0, 
        // // pushes a string param value onto the VM execution stack and returns
        ContractTask<string> CallFromNativeContract(string value)
        {
            using var sb = new ScriptBuilder();
            sb.EmitPush($"{nameof(CallFromNativeContract)} string return");
            sb.EmitSysCall(0);
            sb.EmitPush(value);
            sb.Emit(OpCode.RET);

            var ctx = LoadScript(sb.ToArray());
            return RegisterCompletion<string>(ctx);
        }

        // this is only here for debug output
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
                    StackItemType.ByteString => item.GetString(),
                    StackItemType.Integer => item.GetInteger().ToString(),
                    _ => throw new InvalidOperationException()
                };
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            await Task.Delay(0);
            RunEngine();
        }

        static void RunEngine()
        {
            using var sb = new ScriptBuilder();
            sb.EmitSysCall(1);
            sb.EmitSysCall(2);
            sb.EmitSysCall(3);
            sb.EmitSysCall(4);
            sb.EmitSysCall(5);
            sb.Emit(OpCode.RET);

            var engine = new AppEngine();
            engine.LoadScript(sb.ToArray());
            try
            {
                engine.Execute();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.GetType());
            }

            if (engine.ResultStack.Count > 0)
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
    }
}
