﻿using System;
using System.Threading;
using FFImageLoading.Helpers;
using System.Threading.Tasks;
using System.Linq;
using FFImageLoading.Cache;
using System.IO;

namespace FFImageLoading.Work
{
	public abstract class ImageLoaderTaskBase: IImageLoaderTask, IDisposable
	{
		private static int _streamIndex;
		private static int GetNextStreamIndex()
		{
			return Interlocked.Increment(ref _streamIndex);
		}

		private bool _clearCacheOnOutOfMemory;
		private string _streamKey;
		private bool _isDisposed;

		protected ImageLoaderTaskBase(IMainThreadDispatcher mainThreadDispatcher, IMiniLogger miniLogger, TaskParameter parameters, bool clearCacheOnOutOfMemory)
		{
			_clearCacheOnOutOfMemory = clearCacheOnOutOfMemory;
			CancellationToken = new CancellationTokenSource();
			Parameters = parameters;
			NumberOfRetryNeeded = parameters.RetryCount;
			MainThreadDispatcher = mainThreadDispatcher;
			Logger = miniLogger;
			ConfigureParameters();
		}

		#region IDisposable implementation

		public void Dispose()
		{
			if (!_isDisposed)
			{
				Finish();
				CancellationToken.Dispose();
				_isDisposed = true;
			}
        }

		#endregion

		public void Finish()
		{
			if (Parameters != null)
			{
				Parameters?.OnFinish(this); // should call dispose
				Parameters.Dispose(); // but to be safer let's call it here anyway
			}
		}

		/// <summary>
		/// Gets the parameters used to retrieve the image.
		/// </summary>
		/// <value>The parameters to retrieve the image.</value>
		public TaskParameter Parameters { get; protected set; }

		protected IMainThreadDispatcher MainThreadDispatcher { get; private set; }

		protected IMiniLogger Logger { get; private set; }

		protected CancellationTokenSource CancellationToken { get; private set; }

		public int NumberOfRetryNeeded { get; private set; }

		/// <summary>
		/// Gets or sets a value indicating whether this <see cref="FFImageLoading.Work.ImageLoaderTaskBase"/> is completed.
		/// </summary>
		/// <value><c>true</c> if completed; otherwise, <c>false</c>.</value>
		public bool Completed { get; set; }

		public abstract bool UsesSameNativeControl(IImageLoaderTask task);

		/// <summary>
		/// Gets the cache key for this image loading task.
		/// </summary>
		/// <value>The cache key.</value>
		public virtual string GetKey(string path = null)
		{
			if (HasCustomCacheKey)
				return Parameters.CustomCacheKey + TransformationsKey + DownsamplingKey;

			string baseKey = null;
			if (Parameters.Stream != null)
			{
				if (_streamKey == null)
					_streamKey = "Stream" + GetNextStreamIndex();

				baseKey = _streamKey;
			}
			else
			{
				baseKey = path ?? Parameters.Path;
				if (string.IsNullOrWhiteSpace(baseKey))
					return null; // If path is null then something is wrong, we should not append transformations key
			}

			return baseKey + TransformationsKey + DownsamplingKey;
		}

		/// <summary>
		/// Indicates if memory cache should be used for the request
		/// </summary>
		/// <returns><c>true</c>, if memory cache should be used, <c>false</c> otherwise.</returns>
		/// <param name="path">Path.</param>
		public bool CanUseMemoryCache(string path = null)
		{
			return GetKey(path) != null && (Parameters.Stream == null || HasCustomCacheKey);
		}

		public void Cancel()
		{
			ImageService.RemovePendingTask(this);

			if (!_isDisposed)
			{
				CancellationToken.Cancel();
			}
			Finish();
			Logger.Debug(string.Format("Canceled image generation for {0}", GetKey()));
		}

		public bool IsCancelled
		{
			get
			{
				return _isDisposed || CancellationToken.IsCancellationRequested;
			}
		}

		/// <summary>
		/// Prepares the instance before it runs.
		/// </summary>
		public abstract Task<bool> PrepareAndTryLoadingFromCacheAsync();

		/// <summary>
		/// Cancel current task only if needed
		/// </summary>
		public void CancelIfNeeded()
		{
			if (!IsCancelled && !this.Completed)
				Cancel();
		}

		public async Task RunAsync()
		{
			try
			{
				if (Completed || IsCancelled || ImageService.ExitTasksEarly)
					return;

				GenerateResult generatingImageSucceeded = GenerateResult.Failed;
				Exception ex = null;


				if (Parameters.Stream == null)
				{
					Func<Task> perform = async () =>
						{
							try
							{
								generatingImageSucceeded = await TryGeneratingImageAsync().ConfigureAwait(false);
							}
							catch (OutOfMemoryException oom)
							{
								if(_clearCacheOnOutOfMemory)
								{
									Logger.Error("Received an OutOfMemoryException we will clear the cache", oom);
									ImageCache.Instance.Clear();
								}
								else
								{
									Logger.Error("Received an OutOfMemoryException", oom);
								}
									
								ex = oom;
							}
							catch (Exception ex2)
							{
								Logger.Error("An error occured", ex2);
								ex = ex2;
							}
						};

					await perform().ConfigureAwait(false);

					// Retry logic if needed
					while (generatingImageSucceeded == GenerateResult.Failed && !IsCancelled && !Completed && NumberOfRetryNeeded > 0)
					{
						int retryNumber = Parameters.RetryCount - NumberOfRetryNeeded;
						Logger.Debug(string.Format("Retry loading operation for key {0}, trial {1}", GetKey(), retryNumber));

						if (Parameters.RetryDelayInMs > 0)
							await Task.Delay(Parameters.RetryDelayInMs).ConfigureAwait(false);

						await perform().ConfigureAwait(false);
						NumberOfRetryNeeded--;
					}
				}
				else
				{
					try
					{
						using (var stream = await Parameters.Stream(CancellationToken.Token).ConfigureAwait(false))
						{
							generatingImageSucceeded = await LoadFromStreamAsync(stream).ConfigureAwait(false);
						}
					}
					catch (TaskCanceledException)
					{
						generatingImageSucceeded = GenerateResult.Canceled;
					}
					catch (Exception ex2) 
					{
						Logger.Error("An error occured", ex2);
						ex = ex2;
					}
				}

				if (!IsCancelled && !Completed && generatingImageSucceeded == GenerateResult.Failed)
				{
					if (ex == null)
						ex = new Exception("FFImageLoading is unable to generate image.");

					Parameters?.OnError(ex);
				}
			}
			finally
			{
				ImageService.RemovePendingTask(this);
				Finish();
			}
		}

		/// <summary>
		/// Tries to load requested image from the cache asynchronously.
		/// </summary>
		/// <returns>A boolean indicating if image was loaded from cache.</returns>
		public abstract Task<CacheResult> TryLoadingFromCacheAsync();

		/// <summary>
		/// Loads the image from given stream asynchronously.
		/// </summary>
		/// <returns>An awaitable task.</returns>
		/// <param name="stream">The stream to get data from.</param>
		public abstract Task<GenerateResult> LoadFromStreamAsync(Stream stream);

		protected abstract Task<GenerateResult> TryGeneratingImageAsync();

		protected string TransformationsKey
		{
			get
			{
				if (Parameters.Transformations == null || Parameters.Transformations.Count == 0)
					return string.Empty;

				return ";" + Parameters.Transformations.Select(t => t.Key).Aggregate((a, b) => a + ";" + b);
			}
		}

		protected string DownsamplingKey
		{
			get
			{
				if (Parameters.DownSampleSize == null)
					return string.Empty;

				return string.Concat(";", Parameters.DownSampleSize.Item1, "x", Parameters.DownSampleSize.Item2);
			}
		}

		protected bool HasCustomCacheKey
		{
			get
			{
				return !String.IsNullOrWhiteSpace(Parameters.CustomCacheKey);
			}
		}

		private void ConfigureParameters()
		{
			var successCallback = Parameters.OnSuccess;
			var errorCallback = Parameters.OnError;
			var finishCallback = Parameters.OnFinish;

			// make sure callbacks are invoked on Main thread
			Parameters.Success((s, r) => MainThreadDispatcher.Post(() => successCallback(s, r)));
			Parameters.Error(ex => MainThreadDispatcher.Post(() => errorCallback(ex)));
			Parameters.Finish(scheduledWork =>
				{
					MainThreadDispatcher.Post(() => finishCallback(scheduledWork));
					Parameters.Dispose(); // if Finish is called then Parameters are useless now, we can dispose them so we don't keep a reference to callbacks
				});
		}
	}
}

