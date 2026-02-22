// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TSLib.Audio;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio;

public sealed class FfmpegProducer : IPlayerSource, IDisposable
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private readonly Id id;
	private static readonly Regex FindDurationMatch = new(@"^\s*Duration: (\d+):(\d\d):(\d\d).(\d\d)", Util.DefaultRegexConfig);
	private static readonly Regex IcyMetadataMacher = new("((\\w+)='(.*?)';\\s*)+", Util.DefaultRegexConfig);
	private const string PreLinkConf = "-hide_banner -nostats -threads 1 -i \"";
	private const string PostLinkConf = "\" -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
	private const string LinkConfIcy = "-hide_banner -nostats -threads 1 -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
	private static readonly TimeSpan retryOnDropBeforeEnd = TimeSpan.FromSeconds(3);
	// 超时阈值
	private static readonly TimeSpan dataTimeout = TimeSpan.FromSeconds(2);
	// 最大重试次数
	private const int MaxReconnectAttempts = 3;

	private readonly ConfToolsFfmpeg config;

	public event EventHandler? OnSongEnd;
	public event EventHandler<SongInfoChanged>? OnSongUpdated;
	// 重连事件
	public event EventHandler? ReconnectStarted;
	public event EventHandler? ReconnectFinished;

	private readonly DedicatedTaskScheduler scheduler;
	private FfmpegInstance? ffmpegInstance;
	private CancellationTokenSource? monitorCts;
	private Task? monitorTask;
	public SampleInfo SampleInfo { get; } = SampleInfo.OpusMusic;

	public FfmpegProducer(ConfToolsFfmpeg config, DedicatedTaskScheduler scheduler, Id id)
	{
		this.config = config;
		this.scheduler = scheduler;
		this.id = id;
	}

	public Task AudioStart(string url, TimeSpan? startOff = null)
	{
		StartFfmpegProcess(url, startOff ?? TimeSpan.Zero);
		StartMonitor();
		return Task.CompletedTask;
	}

	public async Task AudioStartIcy(string url)
	{
		if (!(await StartFfmpegProcessIcy(url)).Get(out _, out var error))
		{
			Log.Warn("Failed to start icy stream: {0}", error);
		}
		StartMonitor();
	}

	public void AudioStop()
	{
		StopMonitor();
		// 永久停止：关闭进程并清除实例
		var instance = ffmpegInstance;
		if (instance != null)
		{
			instance.OnMetaUpdated = null;
			instance.Close();
			ffmpegInstance = null;
		}
	}

	public TimeSpan? Length => GetCurrentSongLength();

	public TimeSpan? Position => ffmpegInstance?.AudioTimer.SongPosition;

	public Task Seek(TimeSpan position) { SetPosition(position); return Task.CompletedTask; }

	public int Read(Span<byte> data, out Meta? meta)
	{
		meta = default;
		int read;

		var instance = ffmpegInstance;

		if (instance is null)
			return 0;

		try
		{
			read = instance.FfmpegProcess.StandardOutput.BaseStream.Read(data);
		}
		catch (Exception ex)
		{
			read = 0;
			Log.Debug(ex, "Can't read ffmpeg");
		}

		if (read == 0)
		{
			AssertNotMainScheduler();

			var (ret, triggerEndSafe) = instance.IsIcyStream
				? OnReadEmptyIcy(instance)
				: OnReadEmpty(instance);
			if (ret)
				return 0;

			if (instance.FfmpegProcess.HasExitedSafe())
			{
				Log.Trace("Ffmpeg has exited");
				var expectedStopLength = GetCurrentSongLength();
				var actualStopPosition = instance.AudioTimer.SongPosition;

				// 判断是否正常结束（已播放至接近末尾）
				bool isNaturalEnd = expectedStopLength != TimeSpan.Zero &&
									actualStopPosition + retryOnDropBeforeEnd >= expectedStopLength;

				if (isNaturalEnd)
				{
					// 正常结束，立即停止并触发结束事件
					AudioStop();
					triggerEndSafe = true;
				}
				else if (instance.ReconnectAttempts >= MaxReconnectAttempts)
				{
					// 已达最大重试次数，放弃重连
					AudioStop();
					triggerEndSafe = true;
				}
				else
				{
					// 异常退出但未达重试上限，等待监控线程重连
					return 0;
				}
			}

			if (triggerEndSafe)
			{
				OnSongEnd?.Invoke(this, EventArgs.Empty);
				return 0;
			}
		}
		else
		{
			// 更新最后数据时间
			instance.LastDataTime = DateTime.UtcNow;
		}

		instance.HasTriedToReconnect = false;
		instance.AudioTimer.PushBytes(read);
		return read;
	}

	private (bool ret, bool trigger) OnReadEmpty(FfmpegInstance instance)
	{
		// 进程已退出且未重连过
		if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
		{
			var expectedStopLength = GetCurrentSongLength();
			Log.Trace("Expected song length {0}", expectedStopLength);
			if (expectedStopLength != TimeSpan.Zero)
			{
				var actualStopPosition = instance.AudioTimer.SongPosition;
				Log.Trace("Actual song position {0}", actualStopPosition);
				if (actualStopPosition + retryOnDropBeforeEnd < expectedStopLength)
				{
					Log.Debug("Connection to song lost, retrying at {0}", actualStopPosition);

					ReconnectStarted?.Invoke(this, EventArgs.Empty);

					instance.HasTriedToReconnect = true;
					if (SetPosition(actualStopPosition).Get(out var newInstance, out var error))
					{
						newInstance.HasTriedToReconnect = true;
						ReconnectFinished?.Invoke(this, EventArgs.Empty);
						return (true, false);
					}
					else
					{
						Log.Debug("Retry failed {0}", error);
						// 失败时不触发 ReconnectFinished，等待监控再次尝试
						// 重置标志，让监控可以再次进入
						instance.HasTriedToReconnect = false;
						return (false, false);
					}
				}
			}
		}
		return (false, false);
	}

	private (bool ret, bool trigger) OnReadEmptyIcy(FfmpegInstance instance)
	{
		AssertNotMainScheduler();

		if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
		{
			Log.Debug("Connection to stream lost, retrying...");
			instance.HasTriedToReconnect = true;
			var newInstance = StartFfmpegProcessIcy(instance.ReconnectUrl).Result;
			if (newInstance.Ok)
			{
				newInstance.Value.HasTriedToReconnect = true;
				return (true, false);
			}
			else
			{
				Log.Debug("Retry failed {0}", newInstance.Error);
				instance.HasTriedToReconnect = false;
				return (false, false);
			}
		}
		return (false, false);
	}

	private R<FfmpegInstance, string> SetPosition(TimeSpan value)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);

		var instance = ffmpegInstance;
		if (instance is null)
			return "No instance running";
		if (instance.IsIcyStream)
			return "Cannot seek icy stream";
		var lastLink = instance.ReconnectUrl;
		if (lastLink is null)
			return "No current url active";
		return StartFfmpegProcess(lastLink, value);
	}

	private R<FfmpegInstance, string> StartFfmpegProcess(string url, TimeSpan? offsetOpt)
	{
		StopFfmpegProcess();

		var offset = offsetOpt ?? TimeSpan.Zero;
		string arguments;

		if (offset > TimeSpan.Zero)
		{
			var seek = string.Format(CultureInfo.InvariantCulture, @"{0:hh\:mm\:ss\.fff}", offset);
			// 快速 seek：-ss 放在 -i 之前，加上 -seek_timestamp 1 确保精确
			arguments = $"-hide_banner -nostats -threads 1 -ss {seek} -seek_timestamp 1 -i \"{url}\" -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		}
		else
		{
			arguments = $"-hide_banner -nostats -threads 1 -i \"{url}\" -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
		}

		var newInstance = new FfmpegInstance(
			url,
			new PreciseAudioTimer(SampleInfo)
			{
				SongPositionOffset = offset,
			});

		return StartFfmpegProcessInternal(newInstance, arguments);
	}

	private async Task<R<FfmpegInstance, string>> StartFfmpegProcessIcy(string url)
	{
		StopFfmpegProcess();
		Log.Trace("Start icy-stream request {0}", url);

		try
		{
			var response = await WebWrapper
				.Request(url)
				.WithHeader("Icy-MetaData", "1")
				.UnsafeResponse();

			if (!int.TryParse(response.Headers.GetSingle("icy-metaint"), out var metaint))
			{
				response.Dispose();
				return "Invalid icy stream tags";
			}

			var stream = await response.Content.ReadAsStreamAsync();
			var newInstance = new FfmpegInstance(
				url,
				new PreciseAudioTimer(SampleInfo),
				stream,
				metaint)
			{
				OnMetaUpdated = e => OnSongUpdated?.Invoke(this, e)
			};

			new Thread(() => newInstance.ReadStreamLoop(id))
			{
				Name = $"IcyStreamReader[{id}]",
			}.Start();

			return StartFfmpegProcessInternal(newInstance, LinkConfIcy);
		}
		catch (Exception ex)
		{
			var error = $"Unable to create icy-stream ({ex.Message})";
			Log.Warn(ex, error);
			return error;
		}
	}

	private R<FfmpegInstance, string> StartFfmpegProcessInternal(FfmpegInstance instance, string arguments)
	{
		try
		{
			instance.FfmpegProcess.StartInfo = new ProcessStartInfo
			{
				FileName = config.Path.Value,
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardInput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			instance.FfmpegProcess.EnableRaisingEvents = true;

			Log.Debug("Starting ffmpeg with {0}", arguments);
			instance.FfmpegProcess.ErrorDataReceived += instance.FfmpegProcess_ErrorDataReceived;
			instance.FfmpegProcess.Start();
			instance.FfmpegProcess.BeginErrorReadLine();

			instance.AudioTimer.Start();

			// 成功启动，替换为新实例，并关闭旧实例（旧实例可能已被关闭，但确保清理）
			var oldInstance = Interlocked.Exchange(ref ffmpegInstance, instance);
			oldInstance?.Close(); // 关闭旧实例，但旧实例可能已经被关闭，这里再次关闭无害

			return instance;
		}
		catch (Exception ex)
		{
			var error = ex is Win32Exception
				? $"Ffmpeg could not be found ({ex.Message})"
				: $"Unable to create stream ({ex.Message})";
			Log.Error(ex, error);
			instance.Close();
			// 启动失败，ffmpegInstance 仍指向旧实例（已关闭），不置 null
			return error;
		}
	}

	// 仅关闭进程，不移除引用，供重连时使用
	private void StopFfmpegProcess()
	{
		var instance = ffmpegInstance;
		if (instance != null)
		{
			instance.OnMetaUpdated = null;
			instance.Close(); // 关闭进程，但不置 null
		}
	}

	private TimeSpan? GetCurrentSongLength() => ffmpegInstance?.ParsedSongLength;

	private void AssertNotMainScheduler()
	{
		if (TaskScheduler.Current == scheduler)
			throw new Exception("Cannot read on own scheduler. Throwing to prevent deadlock");
	}

	// 启动监控线程
	private void StartMonitor()
	{
		StopMonitor();
		monitorCts = new CancellationTokenSource();
		var token = monitorCts.Token;
		monitorTask = Task.Run(async () =>
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					await Task.Delay(1000, token).ConfigureAwait(false); // 每1秒检查
					var instance = ffmpegInstance;
					if (instance == null) continue;

					var timeSinceLastData = DateTime.UtcNow - instance.LastDataTime;
					if (timeSinceLastData > dataTimeout && !instance.HasTriedToReconnect)
					{
						instance.HasTriedToReconnect = true;
						instance.ReconnectAttempts++;
						var actualStopPosition = instance.AudioTimer.SongPosition;

						Log.Debug("Monitor: No data for {0} (attempt {1}/{2})", timeSinceLastData, instance.ReconnectAttempts, MaxReconnectAttempts);

						scheduler.Invoke(() =>
						{
							ReconnectStarted?.Invoke(this, EventArgs.Empty);

							// 杀死当前进程（但保留实例）
							try { instance.Close(); } catch { }

							if (instance.ReconnectAttempts <= MaxReconnectAttempts)
							{
								// 尝试重连，传入 actualStopPosition 保持进度
								var result = SetPosition(actualStopPosition);
								if (result.Get(out var newInstance, out var error))
								{
									// 成功，新实例已设置
									newInstance.HasTriedToReconnect = true;
									newInstance.ReconnectAttempts = instance.ReconnectAttempts;
									Log.Debug("Reconnect successful (attempt {0})", instance.ReconnectAttempts);
									ReconnectFinished?.Invoke(this, EventArgs.Empty); // 成功才恢复时钟
								}
								else
								{
									Log.Debug("Reconnect failed (attempt {0}): {1}", instance.ReconnectAttempts, error);
									// 失败，重置标志以便下次监控再次尝试，且不恢复时钟
									instance.HasTriedToReconnect = false;
									// 注意：ReconnectAttempts 已经增加，下次会递增
								}
							}
							else
							{
								Log.Error("Max reconnect attempts ({0}) reached, stopping song.", MaxReconnectAttempts);
								// 已达最大次数，触发结束事件
								OnSongEnd?.Invoke(this, EventArgs.Empty);
								// 不恢复时钟，歌曲将结束
							}
						});
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					Log.Error(ex, "Monitor thread error");
				}
			}
		}, token);
	}

	private void StopMonitor()
	{
		monitorCts?.Cancel();
		monitorTask?.ContinueWith(t => Log.Debug(t.Exception, "Monitor stopped"), TaskContinuationOptions.OnlyOnFaulted);
		monitorCts = null;
		monitorTask = null;
	}

	public void Dispose()
	{
		StopMonitor();
		AudioStop(); // 永久停止
	}

	private class FfmpegInstance
	{
		public Process FfmpegProcess { get; }
		public bool HasTriedToReconnect { get; set; }
		public int ReconnectAttempts { get; set; }  // 重试计数
		public string ReconnectUrl { get; }
		public bool IsIcyStream => IcyStream != null;

		public PreciseAudioTimer AudioTimer { get; }
		public TimeSpan? ParsedSongLength { get; set; } = null;

		public Stream? IcyStream { get; }
		public int IcyMetaInt { get; }
		public bool Closed { get; set; }

		public Action<SongInfoChanged>? OnMetaUpdated;

		// 最后数据时间
		public DateTime LastDataTime { get; set; } = DateTime.UtcNow;

		public FfmpegInstance(string url, PreciseAudioTimer timer) : this(url, timer, null!, 0) { }
		public FfmpegInstance(string url, PreciseAudioTimer timer, Stream icyStream, int icyMetaInt)
		{
			FfmpegProcess = new Process();
			ReconnectUrl = url;
			AudioTimer = timer;
			IcyStream = icyStream;
			IcyMetaInt = icyMetaInt;

			HasTriedToReconnect = false;
			ReconnectAttempts = 0;
		}

		public void Close()
		{
			Closed = true;

			try
			{
				if (!FfmpegProcess.HasExitedSafe())
					FfmpegProcess.Kill();
			}
			catch (Exception ex) { Log.Debug(ex, "Failed killing ffmpeg"); }
			try { FfmpegProcess.Dispose(); } catch { }

			IcyStream?.Dispose();
		}

		public void FfmpegProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
		{
			if (e.Data is null)
				return;

			if (sender != FfmpegProcess)
				throw new InvalidOperationException("Wrong process associated to event");

			if (ParsedSongLength is null)
			{
				var match = FindDurationMatch.Match(e.Data);
				if (!match.Success)
					return;

				int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
				int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
				int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
				int millisec = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 10;
				ParsedSongLength = new TimeSpan(0, hours, minutes, seconds, millisec);
			}
		}

		public void ReadStreamLoop(Id id)
		{
			if (IcyStream is null)
				throw new InvalidOperationException("Instance is not an icy stream");

			Tools.SetLogId(id.ToString());
			const int IcyMaxMeta = 255 * 16;
			const int ReadBufferSize = 4096;

			int errorCount = 0;
			var buffer = new byte[Math.Max(ReadBufferSize, IcyMaxMeta)];
			int readCount = 0;

			while (!Closed)
			{
				try
				{
					while (readCount < IcyMetaInt)
					{
						int read = IcyStream.Read(buffer, 0, Math.Min(ReadBufferSize, IcyMetaInt - readCount));
						if (read == 0)
						{
							Close();
							return;
						}
						readCount += read;
						FfmpegProcess.StandardInput.BaseStream.Write(buffer, 0, read);
						errorCount = 0;
					}
					readCount = 0;

					var metaByte = IcyStream.ReadByte();
					if (metaByte < 0)
					{
						Close();
						return;
					}

					if (metaByte > 0)
					{
						metaByte *= 16;
						while (readCount < metaByte)
						{
							int read = IcyStream.Read(buffer, 0, metaByte - readCount);
							if (read == 0)
							{
								Close();
								return;
							}
							readCount += read;
						}
						readCount = 0;

						var metaString = Tools.Utf8Encoder.GetString(buffer, 0, metaByte).TrimEnd('\0');
						Log.Debug("Meta: {0}", metaString);
						OnMetaUpdated?.Invoke(ParseIcyMeta(metaString));
					}
				}
				catch (Exception ex)
				{
					errorCount++;
					if (errorCount >= 50)
					{
						Log.Error(ex, "Failed too many times trying to access ffmpeg. Closing stream.");
						Close();
						return;
					}

					if (ex is InvalidOperationException)
					{
						Log.Debug(ex, "Waiting for ffmpeg");
						Thread.Sleep(100);
					}
					else
					{
						Log.Debug(ex, "Stream read/write error");
					}
				}
			}
		}

		private static SongInfoChanged ParseIcyMeta(string metaString)
		{
			var songInfo = new SongInfoChanged();
			var match = IcyMetadataMacher.Match(metaString);
			if (match.Success)
			{
				for (int i = 0; i < match.Groups[1].Captures.Count; i++)
				{
					switch (match.Groups[2].Captures[i].Value.ToUpperInvariant())
					{
					case "STREAMTITLE":
						songInfo.Title = match.Groups[3].Captures[i].Value;
						break;
					}
				}
			}
			return songInfo;
		}
	}
}

// Icy: IcyLoop +=> FFmpeg -=> Buffer -=> TimePipe
// Nrm:             FFmpeg -=> Buffer -=> TimePipe
