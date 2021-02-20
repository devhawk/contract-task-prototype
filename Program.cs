using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Neo.VM;
using ExecutionContext = Neo.VM.ExecutionContext;

namespace await_test
{
    // [AsyncMethodBuilder(typeof(ContractTaskMethodBuilder))]
    class ContractTask
    {
        private Action? continuation = null;
        public bool IsCompleted { get; private set; } = false;
        public ContractTaskAwaiter GetAwaiter() => new ContractTaskAwaiter(this);

        public void OnCompleted(Action continuation)
        {
            var value = Interlocked.CompareExchange<Action?>(ref this.continuation, continuation, null);
        }

        public void RunContinuation()
        {
            if (continuation == null || IsCompleted)
            {
                throw new Exception();
            }
            else 
            { 
                continuation(); 
                IsCompleted = true;
            }
        }
    }

    class ContractTaskAwaiter : INotifyCompletion
    {
        private readonly ContractTask contractTask;

        public ContractTaskAwaiter(ContractTask contractTask)
        {
            this.contractTask = contractTask;
        }

        public bool IsCompleted => this.contractTask.IsCompleted;
        public void GetResult() { ; }
        public void OnCompleted(Action continuation) => contractTask.OnCompleted(continuation);
    }

    class AppEngine : ExecutionEngine
    {
        protected override async void OnSysCall(uint method)
        {
            if (method % 2 == 0)
            {
                Console.WriteLine($"OnSysCall Start {method}");
                await CallFromNativeContract(method);
                Console.WriteLine($"OnSysCall End {method}");
            }
            else
            {
                Console.WriteLine($"OnSysCall ODD {method}");
            }
        }

        Dictionary<ExecutionContext, ContractTask> foobar = new Dictionary<ExecutionContext, ContractTask>();

        protected override void ContextUnloaded(ExecutionContext context)
        {
            if (foobar.TryGetValue(context, out var task))
            {
                task.RunContinuation();
                foobar.Remove(context);
            }

            base.ContextUnloaded(context);
        }

        internal ContractTask CallFromNativeContract(BigInteger integer)
        {
            using var sb = new ScriptBuilder();
            sb.EmitSysCall(17);
            sb.Emit(OpCode.RET);
            var script = sb.ToArray();

            var ctx = LoadScript(script);
            var task = new ContractTask();
            foobar.Add(ctx, task);
            return task;
        }

        protected override void PreExecuteInstruction()
        {
            Console.WriteLine($"{InvocationStack.Count} {CurrentContext.GetHashCode()} {CurrentContext.InstructionPointer} {CurrentContext.CurrentInstruction.OpCode}");
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            using var sb = new ScriptBuilder();
            sb.Emit(OpCode.NOP);
            sb.EmitSysCall(1234);
            sb.Emit(OpCode.NOP);
            sb.Emit(OpCode.RET);

            var engine = new AppEngine();
            engine.LoadScript(sb.ToArray());
            engine.Execute();
        }
    }
}



/*
   [AsyncMethodBuilder(typeof(ContractTaskMethodBuilder<>))]
    class ContractTask<T>
    {
        public ContractTaskAwaiter<T> GetAwaiter() => throw new NotImplementedException();
    }

    class ContractTaskAwaiter<T> : INotifyCompletion
    {
        public bool IsCompleted => throw new NotImplementedException();
        public void GetResult() => throw new NotImplementedException();
        public void OnCompleted(Action continuation) => throw new NotImplementedException();
    }

    class ContractTaskMethodBuilder<T>
    {
        public static ContractTaskMethodBuilder<T> Create() => throw new NotImplementedException();
        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
             => throw new NotImplementedException();
        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
        public void SetException(Exception exception) => throw new NotImplementedException();
        public void SetResult() => throw new NotImplementedException();
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
        public ContractTask<T> Task => throw new NotImplementedException();
    }
    */