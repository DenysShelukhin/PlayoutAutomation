using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TAS.Common;
using TAS.Server.MediaOperation;

namespace TAS.Server
{
    public class FileOperationQueue
    {
        private readonly Queue<FileOperationBase> _queue = new Queue<FileOperationBase>();
        
        private readonly HashSet<FileOperationBase> _currentOperations = new HashSet<FileOperationBase>();
        private int _maxConcurrent=1;
        internal int MaxConcurrent
        {
            get=>Volatile.Read(ref _maxConcurrent);
            set{
                if(value<1)throw new ArgumentOutOfRangeException(nameof(value));
                // Wake existing queued work when capacity rises. Lowering the
                // limit drains running work; it never aborts a partial output.
                if(Interlocked.Exchange(ref _maxConcurrent,value)<value)Task.Run(()=>RunOperation());
            }
        }

        public void Enqueue(FileOperationBase operation)
        {
            lock (((ICollection)_queue).SyncRoot)
            {
                _queue.Enqueue(operation);
            }
            Task.Run(() => RunOperation());
        }

        public List<FileOperationBase> GetQueue()
        {
            lock (((ICollection)_queue).SyncRoot)
            {
                var result = _queue.ToList();
                result.AddRange(_currentOperations);
                return result;
            }
        }

        public void CancelPending()
        {
            lock (((ICollection)_queue).SyncRoot)
                _queue.Where(o => o.OperationStatus == FileOperationStatus.Waiting).ToList().ForEach(o => o.Abort());
        }

        public event EventHandler<FileOperationEventArgs> OperationCompleted;

        private async void RunOperation()
        {
            while (true)
            {
                FileOperationBase operation;
                lock (((ICollection)_queue).SyncRoot)
                {
                    if (_currentOperations.Count >= MaxConcurrent)
                        return;
                    if (_queue.Count == 0)
                        return;
                    operation = _queue.Dequeue();
                    if (operation.IsAborted)
                        continue;
                    _currentOperations.Add(operation);
                }
                var success = await operation.Execute();
                lock (((ICollection)_queue).SyncRoot)
                {
                    _currentOperations.Remove(operation);
                    if (!operation.IsAborted)
                        if (!success)
                        {
                            if (operation.TryCount > 0)
                                _queue.Enqueue(operation);
                        }
                }
                if (!success)
                {
                    Thread.Sleep(500);
                    continue;
                }
                OperationCompleted?.Invoke(this, new FileOperationEventArgs(operation));
            }
        }

    }
}
