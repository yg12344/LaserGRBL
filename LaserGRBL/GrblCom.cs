﻿/*
 * Created by SharpDevelop.
 * User: Diego
 * Date: 18/11/2016
 * Time: 18:39
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */
using System;

namespace LaserGRBL
{
	/// <summary>
	/// Description of CommandThread.
	/// </summary>
	public class GrblCom : Tools.ThreadClass
	{
		public enum MacStatus
		{Disconnected, Connecting, Idle, Run, Hold, Door, Home, Alarm, Check}
				
		public delegate void dlgOnMachineStatus(MacStatus status);
		public event dlgOnMachineStatus MachineStatusChanged;

		private System.IO.Ports.SerialPort com;
		private System.Collections.Generic.Queue<GrblCommand> mQueue ;
		private System.Collections.Generic.List<IGrblRow> mSent;
		private System.Collections.Generic.Queue<GrblCommand> mPending ;
		
		private System.Collections.Generic.List<IGrblRow> mSentPtr;
		private System.Collections.Generic.Queue<GrblCommand> mQueuePtr;
		
		private int mBuffer;
		private System.Drawing.PointF mLaserPosition;
		
		private TimeProjection mTP = new TimeProjection();
		
		private MacStatus mMachineStatus;
		private const int BUFFER_SIZE = 127;
		private Version mGrblVersion;
		
		public GrblCom() : base(1, false, "Command Queue Thread")
		{
			com = new System.IO.Ports.SerialPort();
			
			mMachineStatus = MacStatus.Disconnected;
			
			this.com.DataReceived += OnDataReceived;
			mQueue = new System.Collections.Generic.Queue<GrblCommand>();
			mPending = new System.Collections.Generic.Queue<GrblCommand>();
			mSent = new System.Collections.Generic.List<IGrblRow>();
			mBuffer = 0;
			
			mSentPtr = mSent;
			mQueuePtr = mQueue;
			mGrblVersion = null;
		}

		public void ExportConfig(string filename)
		{
			if (mMachineStatus == MacStatus.Idle)
			{
				try
				{
					using (System.IO.StreamWriter sw = new System.IO.StreamWriter(filename))
					{
						GrblCommand cmd = new GrblCommand("$$");
						lock(this)
						{
							mSentPtr =  new System.Collections.Generic.List<IGrblRow>(); //assign sent queue
							mQueuePtr = new System.Collections.Generic.Queue<GrblCommand>();
							mQueuePtr.Enqueue(cmd);
						}

						try
						{
							
							while (cmd.Result == null) //resta in attesa della risposta
								;
							
							if (cmd.Result != "OK")
							{
								System.Windows.Forms.MessageBox.Show("Error exporting config!", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
							}
							else
							{
							
								int msg = 0;
								foreach (IGrblRow row in mSentPtr) 
								{
									if (row is GrblMessage)
									{
										sw.WriteLine(((GrblMessage)row).Message);
										msg++;
									}
								}
								
								sw.Close();
								System.Windows.Forms.MessageBox.Show(String.Format("{0} Config exported with success!", msg), "Success", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
							}
						}catch {System.Windows.Forms.MessageBox.Show("Error exporting config!", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);}
						
						lock(this)
						{
							mQueuePtr = mQueue;
							mSentPtr = mSent; //restore queue
						}
					}
				}
				catch {System.Windows.Forms.MessageBox.Show("Error exporting config!", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);}
			}
		}

		public void ImportConfig(string filename)
		{
			
			if (!System.IO.File.Exists(filename))
			{
				System.Windows.Forms.MessageBox.Show("File does not exist!", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
				return;
			}
			
			if (mMachineStatus == MacStatus.Idle)
			{
				try
				{
					using (System.IO.StreamReader sr = new System.IO.StreamReader(filename))
					{
						lock(this)
						{
							mSentPtr =  new System.Collections.Generic.List<IGrblRow>(); //assign sent queue
							mQueuePtr = new System.Collections.Generic.Queue<GrblCommand>();
							
							string rline = null;
							while ((rline = sr.ReadLine()) != null) 
								if (rline.StartsWith("$"))
									mQueuePtr.Enqueue(new GrblCommand(rline));
						}
						
						try 
						{
							sr.Close();
							while (mQueuePtr.Count > 0 || mPending.Count > 0) //resta in attesa del completamento della comunicazione
								;
							
							int errors = 0;
							foreach (IGrblRow row in mSentPtr) {
								if (row is GrblCommand)
									if (((GrblCommand)row).Result != "OK")
										errors ++;
							}
							
							if (errors > 0)
								System.Windows.Forms.MessageBox.Show(String.Format("{0} Config imported with {1} errors!", mSentPtr.Count, errors) , "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
							else
								System.Windows.Forms.MessageBox.Show(String.Format("{0} Config imported with success!", mSentPtr.Count), "Success", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
						}
						catch {System.Windows.Forms.MessageBox.Show("Error reading file!", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);}
						
						lock(this)
						{
							mQueuePtr = mQueue;
							mSentPtr = mSent; //restore queue
						}
					}
				}
				catch {System.Windows.Forms.MessageBox.Show("Error reading file!", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);}
			}
		}
		
		public void EnqueueProgram(GrblFile prog)
		{
			lock(this)
			{
				ClearQueue(true);
			
				mTP.Reset();
				mTP.JobStart(prog.EstimatedTime, prog.Count);

				foreach (GrblCommand cmd in prog)
					mQueuePtr.Enqueue(cmd.Clone() as GrblCommand);
			}
		}

		public void EnqueueCommand(GrblCommand cmd)
		{
			lock(this)
			{mQueuePtr.Enqueue(cmd.Clone() as GrblCommand);}
		}

		
		public void OpenCom(string port, int baudrate)
		{
			mMachineStatus = MacStatus.Connecting;
			
			if (!com.IsOpen)
			{
				com.PortName = port;
				com.BaudRate = baudrate;
				com.Open();
				com.DiscardOutBuffer();
				com.DiscardInBuffer();		
			}
			
			lock(this)
			{
				GrblReset();
				Start();
			}
		}
		
		public void CloseCom()
		{
			Stop();

			if (com.IsOpen)
			{
				com.DiscardOutBuffer();
				com.DiscardInBuffer();			
				com.Close();
			}
			
			mBuffer = 0;
			
			lock(this)
			{ClearQueue(false);}
			
			mMachineStatus = MacStatus.Disconnected;
		}
		
		public bool IsOpen
		{get{return mMachineStatus != MacStatus.Disconnected;}}
	
		#region Comandi immediati

		public void CycleStartResume()
		{SendImmediate(126);}
		
		public void FeedHold()
		{SendImmediate(33);}
		
		public void SafetyDoor()
		{SendImmediate(64);}
		
		public void QueryPosition()
		{SendImmediate(63);}
		
		public void GrblReset()
		{
			lock(this)
			{
				ClearQueue(true);
				mBuffer = 0;
				mTP.JobEnd();
				
				SendImmediate(24);
			}
		}

		private void SendImmediate(byte b)
		{
			try{
				lock(com)
				{if (com.IsOpen) com.Write(new byte[] { b }, 0, 1);}
			}catch{}
		}
		
		#endregion
		
		#region Public Property
		
		public int ProgramTarget
		{get { return mTP.Target; }}

		public int ProgramSent
		{ get { return mTP.Sent; } }

		public int ProgramExecuted
		{ get { return mTP.Executed; } }
		
		public TimeSpan ProgramTime
		{ get { return mTP.TotalJobTime; } }
		
		public TimeSpan ProjectedTime
		{ get { return mTP.ProjectedTarget; } }

		public MacStatus MachineStatus
		{ get { return mMachineStatus; } }

		public bool InProgram
		{ get { return mTP.InProgram; } }
		
		public System.Drawing.PointF LaserPosition
		{ get { return mLaserPosition; } }
		
		public int Executed
		{ get { return mSent.Count; } }

		public System.Collections.Generic.List<IGrblRow> SentCommand(int index, int count)
		{
			System.Collections.Generic.List<IGrblRow> rv;
			rv = mSent.GetRange(index, count);
			return rv;
		}
		
		#endregion

		#region Grbl Version Support
		
		public bool SupportRTO
		{ get {return mGrblVersion != null && mGrblVersion >= new Version(1,1); }}

		public bool SupportLaserMode
		{ get {return mGrblVersion != null && mGrblVersion >= new Version(1,1); }}
		
		public bool SupportJogging
		{ get {return mGrblVersion != null && mGrblVersion >= new Version(1,1); }}
		
		public bool SupportCSV
		{ get {return mGrblVersion != null && mGrblVersion >= new Version(1,1); }}		
		
		#endregion
		
		private long lastPosRequest;
		protected override void DoTheWork()
		{
			lock(this)
			{
				if (CanSend())
					SendLine();
				
				long now = Tools.HiResTimer.TotalMilliseconds;
				if (now - lastPosRequest > 200)
				{
					QueryPosition();
					lastPosRequest = now;
				}
			}
		}
		
		private bool CanSend()
		{
			GrblCommand next = mQueuePtr.Count > 0 ? mQueuePtr.Peek() : null;
			return next != null && (mBuffer + next.Command.Length) < BUFFER_SIZE;
		}

		private void SendLine()
		{
			try
			{
				GrblCommand tosend = mQueuePtr.Count > 0 ? mQueuePtr.Peek() : null;

				if (tosend != null)
				{
					tosend.SetSending();
					mSentPtr.Add(tosend);
					mPending.Enqueue(tosend);
					mQueuePtr.Dequeue();
				
					mBuffer += (tosend.Command.Length +2); //+2 for \r\n
					com.WriteLine(tosend.Command);

					if (mTP.InProgram)
						mTP.JobSent();
				}
			}
			catch {}
		}

		private	char[] trimarray = new char[]  {'\r', '\n', ' '};
		void OnDataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
		{
			try
			{
				string rline = null;
				while ((rline = com.ReadLine()) != null && rline.Length > 0)
				{
					rline = rline.TrimEnd(trimarray); //rimuovi ritorno a capo
					rline = rline.Trim(); //rimuovi spazi iniziali e finali
					if (rline.Length > 0)
					{
						lock(this)
						{
							if (rline.ToLower().StartsWith("ok") || rline.ToLower().StartsWith("error"))
							{
								if (mPending.Count > 0)
								{
									GrblCommand pending = mPending.Dequeue();
	
									//mLaserPosition = new System.Drawing.PointF(pending.X != null ? (float)pending.X.Number : mLaserPosition.X, pending.Y != null ? (float)pending.Y.Number : mLaserPosition.Y);
	
									pending.SetResult(rline, SupportCSV);
									mBuffer -= (pending.Command.Length + 2); //+2 for \r\n

									if (mTP.InProgram)
										mTP.JobExecuted(pending.TimeOffset);

									//if (mQueue.Count == 0 && mPending.Count == 0)
									//{
									//	if (QueueStatus != null)
									//		QueueStatus(true);
									//}
								}
							}
							else if (rline.StartsWith("<") && rline.EndsWith(">"))
							{
								rline = rline.Substring(1, rline.Length - 2);
								
								if (rline.Contains("|")) //grbl > 1.1
								{
									string[] arr = rline.Split("|".ToCharArray());
									
									ParseMachineStatus(arr[0]);
									string mpos = arr[1].Substring(5, arr[1].Length - 5);
									string[] xyz = mpos.Split(",".ToCharArray());
									mLaserPosition = new System.Drawing.PointF(float.Parse(xyz[0], System.Globalization.NumberFormatInfo.InvariantInfo), float.Parse(xyz[1], System.Globalization.NumberFormatInfo.InvariantInfo));
								}
								else //<Idle,MPos:0.000,0.000,0.000,WPos:0.000,0.000,0.000>
								{
									string[] arr = rline.Split(",".ToCharArray());
									
									ParseMachineStatus(arr[0]);
									mLaserPosition = new System.Drawing.PointF(float.Parse(arr[1].Substring(5, arr[1].Length -5), System.Globalization.NumberFormatInfo.InvariantInfo), float.Parse(arr[2], System.Globalization.NumberFormatInfo.InvariantInfo));
								}
								
							}
							else if (rline.StartsWith("Grbl "))
							{
								//Grbl vX.Xx ['$' for help]
								try
								{
									int maj = int.Parse(rline.Substring(5,1));
									int min = int.Parse(rline.Substring(7,1));
									int build = (int)(rline.Substring(8,1).ToCharArray()[0]);
									
									mGrblVersion = new Version(maj, min, build);
								}
								catch {}
								mSentPtr.Add(new GrblMessage(rline, false));
							}
							else
							{
								mSentPtr.Add(new GrblMessage(rline, SupportCSV));
							}
						}
					}
				}
			} catch {}
		}

		private void ParseMachineStatus(string data)
		{
			MacStatus var = MacStatus.Disconnected;
			
			if (data.Contains(":"))
				data = data.Substring(0, data.IndexOf(':'));
			
			Enum.TryParse(data, out var);
			
			if (var == MacStatus.Idle && mQueuePtr.Count == 0 && mPending.Count == 0)
				mTP.JobEnd();
			
			if (mTP.InProgram && var == MacStatus.Idle) //bugfix for grbl sending Idle on G4
				var = MacStatus.Run;
			
			if (mMachineStatus != var)
			{
				mMachineStatus = var;

				if (MachineStatusChanged != null)
					MachineStatusChanged(mMachineStatus);
				
				if (mTP.InProgram)
				{
					if (InPause)
						mTP.JobPause();
					else
						mTP.JobResume();
				}
			}

		}

		private bool InPause
		{ get { return mMachineStatus != MacStatus.Run && mMachineStatus != MacStatus.Idle; } }
		
		private void ClearQueue(bool sent)
		{
			mQueue.Clear();
			mPending.Clear();
			if (sent) mSent.Clear();
		}
	}

	public class TimeProjection
	{
		private TimeSpan mETarget;
		private TimeSpan mEProgress;

		private long mStart;		//Start Time
		private long mEnd;			//End Time
		private long mPauseBegin;	//Pause begin Time
		private long mCumulatedPause;

		private bool mInPause;
		private bool mCompleted;
		private bool mStarted;

		private int mTargetCount;
		private int mExecutedCount;
		private int mSentCount;

		public TimeProjection()
		{Reset();}

		public void Reset()
		{
			mStart = 0;
			mEnd = 0;
			mPauseBegin = 0;
			mCumulatedPause = 0;
			mInPause = false;
			mCompleted = false;
			mStarted = false;
		}

		public TimeSpan EstimatedTarget
		{
			get { return mETarget; }
			//set { mETarget = value; }
		}

		//public TimeSpan EstimatedProgress
		//{
		//	get { return mEProgress; }
		//	//set { mEProgress = value; }
		//}

		public bool InProgram
		{ get { return mStarted && !mCompleted; } }

		public int Target
		{ get { return mTargetCount; } }

		public int Sent
		{ get { return mSentCount; } }

		public int Executed
		{ get { return mExecutedCount; } }

		public TimeSpan ProjectedTarget
		{
			get
			{
				if (mStarted)
				{
					double real = TrueJobTime.TotalSeconds;	//job time spent in execution
					double target = mETarget.TotalSeconds;	//total estimated
					double done = mEProgress.TotalSeconds;	//done of estimated

					if (done != 0)
						return TimeSpan.FromSeconds(real * target / done) + TotalJobPauses;
					else
						return EstimatedTarget;
				}
				else
					return TimeSpan.Zero;
			}
		}

		private TimeSpan TrueJobTime
		{ get { return TotalJobTime - TotalJobPauses; } }

		public TimeSpan TotalJobTime
		{
			get	
			{
				if (mCompleted)
					return TimeSpan.FromMilliseconds(mEnd - mStart);
				else if (mStarted)
					return TimeSpan.FromMilliseconds(now - mStart);
				else
					return TimeSpan.Zero;
			}
		}

		private TimeSpan TotalJobPauses
		{
			get 
			{
				if (mInPause)
					return TimeSpan.FromMilliseconds(mCumulatedPause + (now - mPauseBegin));
				else
					return TimeSpan.FromMilliseconds(mCumulatedPause);
			}
		}

		public void JobStart(TimeSpan EstimatedTarget, int LineCount)
		{
			if (!mStarted)
			{
				mETarget = EstimatedTarget;
				mTargetCount = LineCount;
				mEProgress = TimeSpan.Zero;
				mStart = Tools.HiResTimer.TotalMilliseconds;
				mPauseBegin = 0;
				mInPause = false;
				mCompleted = false;
				mStarted = true;
				mExecutedCount = 0;
				mSentCount = 0;
			}
		}

		public void JobSent()
		{
			if (mStarted && !mCompleted)
				mSentCount++;
		}

		public void JobExecuted(TimeSpan EstimatedProgress)
		{
			if (mStarted && !mCompleted)
			{
				mExecutedCount++;
				mEProgress = EstimatedProgress;
			}
		}

		public void JobPause()
		{
			if (mStarted && !mCompleted && !mInPause)
			{
				mInPause = true;
				mPauseBegin = now;
			}
		}

		public void JobResume()
		{
			if (mStarted && !mCompleted && mInPause)
			{
				mCumulatedPause += Tools.HiResTimer.TotalMilliseconds - mPauseBegin;
				mInPause = false;
			}
		}

		public void JobEnd()
		{
			if (mStarted && !mCompleted)
			{
				JobResume(); //nel caso l'ultimo comando fosse una pausa, la chiudo e la cumulo
				mEnd = Tools.HiResTimer.TotalMilliseconds;
				mCompleted = true;
				mStarted = false;
			}
		}

		private long now
		{ get { return Tools.HiResTimer.TotalMilliseconds; } }
	}
}

/*
Idle: All systems are go, no motions queued, and it's ready for anything.
Run: Indicates a cycle is running.
Hold: A feed hold is in process of executing, or slowing down to a stop. After the hold is complete, Grbl will remain in Hold and wait for a cycle start to resume the program.
Door: (New in v0.9i) This compile-option causes Grbl to feed hold, shut-down the spindle and coolant, and wait until the door switch has been closed and the user has issued a cycle start. Useful for OEM that need safety doors.
Home: In the middle of a homing cycle. NOTE: Positions are not updated live during the homing cycle, but they'll be set to the home position once done.
Alarm: This indicates something has gone wrong or Grbl doesn't know its position. This state locks out all G-code commands, but allows you to interact with Grbl's settings if you need to. '$X' kill alarm lock releases this state and puts Grbl in the Idle state, which will let you move things again. As said before, be cautious of what you are doing after an alarm.
Check: Grbl is in check G-code mode. It will process and respond to all G-code commands, but not motion or turn on anything. Once toggled off with another '$C' command, Grbl will reset itself.
*/
