﻿/*
* Copyright (c) 2021 PSPACE, inc. KSAN Development Team ksan@pspace.co.kr
* KSAN is a suite of free software: you can redistribute it and/or modify it under the terms of
* the GNU General Public License as published by the Free Software Foundation, either version 
* 3 of the License.  See LICENSE for details
*
* 본 프로그램 및 관련 소스코드, 문서 등 모든 자료는 있는 그대로 제공이 됩니다.
* KSAN 프로젝트의 개발자 및 개발사는 이 프로그램을 사용한 결과에 따른 어떠한 책임도 지지 않습니다.
* KSAN 개발팀은 사전 공지, 허락, 동의 없이 KSAN 개발에 관련된 모든 결과물에 대한 LICENSE 방식을 변경 할 권리가 있습니다.
*/
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using log4net;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using IfsSync2Data;
using System.Collections.Generic;
using System.Diagnostics;
using Amazon.S3.Transfer;
using Amazon;

namespace IfsSync2Sender
{
    public class SenderThread
    {
        private readonly string CLASS_NAME = "IfsSync2Sender";
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public int Delay { get; set; }

        private readonly TaskDataSqlManager TaskSQL = null;
        private readonly JobDataSqlManager JobSQL = null;

        public JobData Job { get; set; }
        private readonly Thread Sender = null;
        private readonly JobState State;
        
        InstantData Instant = null;
        /*************************AmazonS3*****************************/
        private const string CONNECTFAILURE = "ConnectFailure";
        private const string RENAME_HEADER = "x-amz-ifs-rename";
        private AmazonS3Client Client { get; set; }
        private readonly UserData User = new UserData();
        /****************************ETC*******************************/
        public bool Login_OK { get; set; }
        public bool Quit { get; set; }
        public bool Stop { get; set; }
        public bool IsAlive { get; set; }
        public int FetchCount { get; set; }
        public readonly bool IsGlobal = false;

        public SenderThread(string RootPath, JobData jobData, UserData userData, int FetchCount, int DelayTime, bool IsGlobal)
        {
            Login_OK = false;
            Quit = false;
            Stop = false;
            Job = jobData;
            User = userData;
            Delay = DelayTime;
            this.FetchCount = FetchCount;
            this.IsGlobal = IsGlobal;
            TaskSQL = new TaskDataSqlManager(RootPath, Job.HostName, Job.JobName);
            JobSQL = new JobDataSqlManager(RootPath);
            State = new JobState(Job.HostName, Job.JobName, true);
            CLASS_NAME += string.Format(":{0}:{1}", Job.HostName, Job.JobName);

            IsAlive = true;
            Sender = new Thread(()=>Start());
            Sender.Start();
        }

        public bool Login()
        {
            const string FUNCTION_NAME = "Login";
            const string LOGIN_FAIL_MESSAGE = "로그인실패! 네트워크 연결이 끊어졌거나 관리자의 접속허가가 필요할 수 있습니다.";
            try
            {
                if (User == null)
                {
                    log.FatalFormat("[{0}:{1}:{2}] User Data is not exist", CLASS_NAME, FUNCTION_NAME);
                    End();
                    return false;
                }
                
                AmazonS3Config config = null;

                if (User.URL.StartsWith(MainData.HTTP, StringComparison.OrdinalIgnoreCase))
                {
                    if (!User.URL.EndsWith("/")) User.URL += "/";
                    config = new AmazonS3Config
                    {
                        ServiceURL = User.URL,
                        SignatureVersion = "2",
                        ReadWriteTimeout = TimeSpan.FromSeconds(MainData.SENDER_TIMEOUT),
                        Timeout = TimeSpan.FromSeconds(MainData.SENDER_TIMEOUT),
                        ForcePathStyle = true
                    };
                }
                else
                {
                    config = new AmazonS3Config
                    {
                        RegionEndpoint = RegionEndpoint.GetBySystemName(User.URL),
                        SignatureVersion = "2",
                        ReadWriteTimeout = TimeSpan.FromSeconds(MainData.SENDER_TIMEOUT),
                        Timeout = TimeSpan.FromSeconds(MainData.SENDER_TIMEOUT),
                        ForcePathStyle = true
                    };
                }

                Client = new AmazonS3Client(User.AccessKey, User.AccessSecret, config);
                if (CheckConnect(Client))
                {
                    log.InfoFormat("[{0}:{1}] Login Success : {2}", CLASS_NAME, FUNCTION_NAME, Job.JobName);
                    if (State.Error) TaskSQL.InsertLog("S3 Login Success!");
                    Login_OK = true;
                    State.Error = false;
                }
                else
                {
                    log.FatalFormat("[{0}:{1}:{2}] Login fail : {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Client", User.URL);
                    Login_OK = false;
                    if (!State.Error) TaskSQL.InsertLog(LOGIN_FAIL_MESSAGE);
                    State.Error = true;
                    End();
                }
                return Login_OK;
            }
            catch (AmazonS3Exception e)
            {
                log.FatalFormat("[{0}:{1}:{2}] Login fail : {3}", CLASS_NAME, FUNCTION_NAME, e.Message);
                if (!State.Error) TaskSQL.InsertLog(LOGIN_FAIL_MESSAGE);
                State.Error = true;
                return false;
            }
            catch(Exception e)
            {
                string Message;
                if (e.Message.Contains(CONNECTFAILURE)) Message = "The network connection has been lost.";
                else                                    Message = "S3 Login Fail!";
                
                log.FatalFormat("[{0}:{1}:{2}] {3} : {4}", CLASS_NAME, FUNCTION_NAME, "Exception", Message, e.Message);
                if (!State.Error) TaskSQL.InsertLog(Message);

                State.Error = true;
                return false;
            }
        }

        public void Start()
        {
            const string FUNCTION_NAME = "Start";

            log.DebugFormat("[{0}:{1}] Start", CLASS_NAME, FUNCTION_NAME);

            if (!Login())
            {
                End();
                return;
            }

            switch (Job.Policy)
            {
                case JobData.PolicyNameList.Now: {
                        //Analysis and Backup
                        InstantBackup();
                        break;
                    }
                case JobData.PolicyNameList.Schedule: {
                        if (Job.IsInit)
                        {
                            Analysis();
                            JobSQL.UpdateIsinit(Job, false, IsGlobal);
                        }
                        while (!State.Quit)
                        {
                            if (!Job.CheckToSchedules()) break;
                            
                            if (!Stop) RunOnce();
                            Thread.Sleep(Delay);
                        }
                        break;
                    }
                case JobData.PolicyNameList.RealTime: {
                        if (Job.IsInit)
                        {
                            Analysis();
                            JobSQL.UpdateIsinit(Job, false, IsGlobal);
                        }
                        while (!State.Quit)
                        {
                            if (!Stop) RealTimeRunOnce();
                            Thread.Sleep(Delay);
                        }
                        break;
                    }
            }
            End();
        }
        public void End()
        {
            IsAlive = false;
            Stop = true;
            Quit = true;
            State.Sender = false;
        }
        public void Close()
        {
            const string FUNCTION_NAME = "Close";
            End();
            log.DebugFormat("[{0}:{1}] Close", CLASS_NAME, FUNCTION_NAME);
        }

        private bool CheckNeedVSS()
        {
            if (TaskSQL.TaskCount == 0) return false;
            if (Job.VSSFileExt.Count == 0) return false;

            foreach (TaskData Data in TaskSQL.TaskDatas)
            {
                foreach (string Ext in Job.VSSFileExt)
                {
                    if (Data.FilePath.EndsWith(Ext)) return true;
                }
            }

            return false;
        }

        public void RealTimeRunOnce()
        {
            State.Sender = true;
            string BucketName = User.UserName.ToLower().Replace("_", "-");
            List<ShadowCopy> ShadowCopyList = null;
            
            while (!State.Quit)
            {
                TaskSQL.GetList(FetchCount);
                if (TaskSQL.TaskCount == 0) break;
                State.RenameUpdate(TaskSQL.TaskCount, TaskSQL.TaskSize);
                
                if (CheckNeedVSS())
                {
                    if (ShadowCopyList == null) ShadowCopyList = GetShadowCopies();
                }

                foreach (TaskData item in TaskSQL.TaskDatas)
                {
                    if (State.Quit || Stop) { break; }

                    //Get Snapshot Path
                    if (State.VSS && item.TaskName == TaskData.TaskNameList.Upload)
                    {
                        foreach (ShadowCopy Shadow in ShadowCopyList)
                            if (item.FilePath.StartsWith(Shadow.VolumeName)) item.SnapshotPath = Shadow.GetSnapshotPath(item.FilePath);
                    }

                    if (!BackupStart(BucketName, item))
                    {
                        End();
                        break;
                    }
                }

                if (State.Quit || Stop) break;
                else Thread.Sleep(Delay);
            }
            ReleaseShadowCopies(ShadowCopyList);
            State.Sender = false;
        }
        private void InstantRunOnce(List<ShadowCopy> ShadowCopyList = null)
        {
            State.Sender = true;
            string BucketName = User.UserName.ToLower().Replace("_", "-");

            while (!State.Quit)
            {
                TaskSQL.GetList(FetchCount);
                if (TaskSQL.TaskCount == 0) break;
                State.RenameUpdate(TaskSQL.TaskCount, TaskSQL.TaskSize);

                if (ShadowCopyList == null)
                {
                    ShadowCopyList = GetShadowCopies();
                }

                foreach (TaskData item in TaskSQL.TaskDatas)
                {
                    if (State.Quit || Stop || !Job.CheckToSchedules()) { break; }

                    //Get Snapshot Path Only Upload
                    if (item.TaskName == TaskData.TaskNameList.Upload)
                    {
                        foreach (ShadowCopy Shadow in ShadowCopyList)
                        {
                            if (item.FilePath.StartsWith(Shadow.VolumeName))
                            {
                                item.SnapshotPath = Shadow.GetSnapshotPath(item.FilePath);
                                break;
                            }
                        }
                    }

                    if (!BackupStart(BucketName, item))
                    {
                        End();
                        break;
                    }
                    Instant.Upload++;
                    {
                        //몫
                        double Percent = (double)Instant.Upload / (double)Instant.Total;
                        double quotient = Math.Truncate(Percent);

                        if (Instant.Percent < quotient)
                        {
                            Instant.Percent = (long)quotient;
                            TaskSQL.InsertLog(string.Format("Instant Backup : {0:0.##}%", Percent * 100));
                        }
                    }
                }

                if (State.Quit || Stop) break;
                else Thread.Sleep(Delay);
            }
            ReleaseShadowCopies(ShadowCopyList);
            State.Sender = false;
        }
        private void RunOnce(List<ShadowCopy> ShadowCopyList = null)
        {
            State.Sender = true;
            string BucketName = User.UserName.ToLower().Replace("_", "-");
            
            while (!State.Quit)
            {
                TaskSQL.GetList(FetchCount);
                if (TaskSQL.TaskCount == 0) break;
                State.RenameUpdate(TaskSQL.TaskCount, TaskSQL.TaskSize);

                if (ShadowCopyList == null) ShadowCopyList = GetShadowCopies();

                foreach (TaskData item in TaskSQL.TaskDatas)
                {
                    if (State.Quit || Stop || !Job.CheckToSchedules()) { break; }

                    //Get Snapshot Path Only Upload
                    if(item.TaskName == TaskData.TaskNameList.Upload)
                    {
                        foreach (ShadowCopy Shadow in ShadowCopyList)
                        {
                            if (item.FilePath.StartsWith(Shadow.VolumeName))
                            {
                                item.SnapshotPath = Shadow.GetSnapshotPath(item.FilePath);
                                break;
                            }
                        }
                    }

                    if (!BackupStart(BucketName, item))
                    {
                        End();
                        break;
                    }
                }

                if (State.Quit || Stop) break;
                else Thread.Sleep(Delay);
            }
            ReleaseShadowCopies(ShadowCopyList);
            State.Sender = false;
        }

        private List<ShadowCopy> GetShadowCopies()
        {
            const string FUNCTION_NAME = "GetshadowCopies";

            //find volume
            List<string> VolumeList = new List<string>();
            foreach (var Directory in Job.Path)
            {
                string root = Path.GetPathRoot(Directory);
                if (MainData.CheckUNCFolder(root)) continue;
                if (!VolumeList.Contains(root)) VolumeList.Add(root);
            }

            // NTFS and Local Disk
            List<ShadowCopy> ShadowCopys = new List<ShadowCopy>();
            foreach (string Item in VolumeList)
            {
                try
                {
                    DriveInfo drive = new DriveInfo(Item);
                    if (drive.DriveFormat == "NTFS" && drive.DriveType == DriveType.Fixed)
                    {
                        ShadowCopy Shadow = new ShadowCopy();
                        Shadow.Init();
                        Shadow.Setup(Item);
                        ShadowCopys.Add(Shadow);
                    }

                    log.DebugFormat("[{0}:{1}] Shadow Copy Directory : {2}", CLASS_NAME, FUNCTION_NAME, Item);
                }
                catch (Exception e)
                {
                    log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                }
            }
            if (ShadowCopys.Count > 0)
            {
                State.VSS = true;
                TaskSQL.InsertLog("VSS Activation");
            }

            return ShadowCopys;
        }
        private void ReleaseShadowCopies(List<ShadowCopy> ShadowCopies)
        {
            if (ShadowCopies != null)
            {
                if(ShadowCopies.Count > 0)
                {
                    foreach (var Shadow in ShadowCopies) Shadow.Dispose();
                    ShadowCopies.Clear();
                    
                    State.VSS = false;
                    TaskSQL.InsertLog("VSS Deactivation and deletion");
                }

            }
        }

        private bool BackupStart(string BucketName, TaskData task)
        {
            const string FUNCTION_NAME = "BackupStart";

            if (!CheckBucket(Client, BucketName))
            {
                log.InfoFormat("[{0}:{1}] Not exists Bucket : {2}", CLASS_NAME, FUNCTION_NAME, BucketName);
                if (PutBucket(Client, BucketName)) log.InfoFormat("[{0}:{1}] Create Bucket : {2}", CLASS_NAME, FUNCTION_NAME, BucketName);
                else
                {
                    log.ErrorFormat("[{0}:{1}:{2}] Create Fail Bucket {3}", CLASS_NAME, FUNCTION_NAME, "", BucketName);
                    if (!State.Error)
                    {
                        log.ErrorFormat("[{0}:{1}:{2}] The network connection has been lost. {3}", CLASS_NAME, FUNCTION_NAME, "", BucketName);
                        TaskSQL.InsertLog("The network connection has been lost.");
                    }
                    State.Error = true;
                    return false;
                }
            }

            switch (task.TaskName)
            {
                case TaskData.TaskNameList.Upload:
                    {
                        if (string.IsNullOrWhiteSpace(task.SnapshotPath)) task.SnapshotPath = task.FilePath;

                        //md5 Check
                        string S3MD5 = GetObjectMD5(Client, BucketName, task.FilePath);
                        if(!string.IsNullOrWhiteSpace(S3MD5))
                        {
                            string MD5 = MainData.CalculateMD5(task.SnapshotPath);
                            if(S3MD5.Equals(MD5, StringComparison.OrdinalIgnoreCase))
                            {
                                TaskSQL.Delete(task);
                                log.DebugFormat("[{0}:{1}] Duplicate file : {2}", CLASS_NAME, FUNCTION_NAME, task.FilePath);
                                return true;
                            }
                        }

                        if(PutObject(Client, BucketName, task.FilePath, task.SnapshotPath, out string ErrorMsg)) task.UploadFlag = true;
                        else
                        {
                            if (State.Error) return false;
                            task.UploadFlag = false;
                            task.Result = ErrorMsg;
                        }
                        task.UploadTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        TaskSQL.Update(task);
                        break;
                    }
                case TaskData.TaskNameList.Rename:
                    {
                        if (RenameObject(Client, BucketName, task.FilePath, task.NewFilePath, out string ErrorMsg)) task.UploadFlag = true;
                        else
                        {
                            if (State.Error) return false;
                            task.UploadFlag = false;
                            task.Result = ErrorMsg;
                        }
                        task.UploadTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        TaskSQL.Update(task);
                        break;
                    }
                case TaskData.TaskNameList.Delete:
                    {
                        if (DeleteObject(Client, BucketName, task.FilePath, out string ErrorMsg)) task.UploadFlag = true;
                        else
                        {
                            if (State.Error) return false;
                            task.UploadFlag = false;
                            task.Result = ErrorMsg;
                        }
                        
                        task.UploadTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        TaskSQL.Update(task);
                        break;
                    }
            }

            if(task.UploadFlag) State.UploadSuccess(task.FileSize);
            else                State.UploadFail();

            return true;
        }

        #region Amazon S3
        private string FileNameChangeToS3ObjectName(string DirectoryName)
        {
            return DirectoryName.Replace("\\", "/").Replace(":", "");
        }

        private bool CheckConnect(AmazonS3Client client)
        {
            const string FUNCTION_NAME = "CheckConnect";
            try
            {
                var response = client.ListBuckets();
                return true;
            }
            catch (AmazonS3Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}]{3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                return false;
            }
            catch (Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}]{3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                return false;
            }
        }
        private bool CheckBucket(AmazonS3Client client, string BucketName)
        {
            const string FUNCTION_NAME = "CheckBucket";
            try
            {
                var isBucket = AmazonS3Util.DoesS3BucketExistV2(client, BucketName); //Create Check
                log.DebugFormat("[{0}:{1}] BucketName : {2}, Result : {3}", CLASS_NAME, FUNCTION_NAME, BucketName, isBucket);
                return isBucket;
            }
            catch (AmazonS3Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                return false;
            }
            catch (Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                return false;
            }
        }
        private bool PutBucket(AmazonS3Client client, string BucketName)
        {
            const string FUNCTION_NAME = "PutBucket";
            try
            {
                var putBucketRequest = new PutBucketRequest { BucketName = BucketName };
                var response = client.PutBucket(putBucketRequest);//Create Bucket request
                var isBucket = AmazonS3Util.DoesS3BucketExistV2(client, BucketName); //Create Check

                log.DebugFormat("[{0}:{1}] BucketName : {2}, Result : {3}", CLASS_NAME, FUNCTION_NAME, BucketName, isBucket);
                return isBucket;
            }
            catch (AmazonS3Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                return false;
            }
            catch (Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                return false;
            }
        }
        private bool PutObject(AmazonS3Client client, string BucketName, string FilePath, string SnapshotPath, out string ErrorMsg)
        {
            string FUNCTION_NAME = "PutObject";
            try
            {
                if (string.IsNullOrEmpty(SnapshotPath)) SnapshotPath = FilePath;
                if (!File.Exists(SnapshotPath))
                {
                    ErrorMsg = string.Format("[{0}:{1}:{2}] File is not exists : {3}", CLASS_NAME, FUNCTION_NAME, "Exception", SnapshotPath);
                    log.Error(ErrorMsg);
                    return false;
                }

                string ObjectName = FileNameChangeToS3ObjectName(FilePath);

                if (new FileInfo(SnapshotPath).Length > MainData.UPLOAD_CHANGE_FILE_SIZE)
                {
                    FUNCTION_NAME = "Upload";

                    TransferUtility transferUtility = new TransferUtility(client);
                    var UploadRequest = new TransferUtilityUploadRequest
                    {
                        BucketName = BucketName,
                        Key = ObjectName,
                        FilePath = SnapshotPath,
                        PartSize = MainData.UPLOAD_PART_SIZE
                    };
                    transferUtility.Upload(UploadRequest);
                }
                else
                {
                    var putObjectRequest = new PutObjectRequest
                    {
                        BucketName = BucketName,
                        Key = ObjectName,
                        FilePath = SnapshotPath
                    };

                    var response = client.PutObject(putObjectRequest);
                }
                log.DebugFormat("[{0}:{1}] BucketName : {2} ObjectName : {3}", CLASS_NAME, FUNCTION_NAME, BucketName, ObjectName);
                ErrorMsg = string.Empty;
                return true;
            }
            catch (AmazonS3Exception e)
            {
                ErrorMsg = string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                log.Error(ErrorMsg);
                return false;
            }
            catch (Exception e)
            { 
                ErrorMsg = string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                log.Error(ErrorMsg);
                if (e.Message.Contains(CONNECTFAILURE))
                {
                    string Message = "The network connection has been lost.";
                    log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", Message);
                    if (!State.Error) TaskSQL.InsertLog(Message);
                    State.Error = true;
                }
                return false;
                //throw new Exception(string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message));
            }
        }
        private bool RenameObject(AmazonS3Client client, string BucketName, string FilePath, string NewFilePath, out string ErrorMsg)
        {
            const string FUNCTION_NAME = "RenameObject";
            try
            {
                string ObjectName = FileNameChangeToS3ObjectName(FilePath);
                string NewObjectName = FileNameChangeToS3ObjectName(NewFilePath);

                var copyObjectRequest = new CopyObjectRequest
                {
                    SourceBucket = BucketName,
                    DestinationBucket = BucketName,
                    SourceKey = ObjectName,
                    DestinationKey = NewObjectName
                };
                copyObjectRequest.Headers[RENAME_HEADER] = "None";

                var response = client.CopyObject(copyObjectRequest);
                log.DebugFormat("[{0}:{1}] BucketName : {2} ObjectName : {3} NewObjectName : {4}", CLASS_NAME, FUNCTION_NAME, BucketName, ObjectName, NewObjectName);
                ErrorMsg = string.Empty;
                return true;
            }
            catch (AmazonS3Exception e)
            {
                ErrorMsg = string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                log.Error(ErrorMsg);
                return false;
            }
            catch (Exception e)
            {
                ErrorMsg = string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                log.Error(ErrorMsg);
                if (e.Message.Contains(CONNECTFAILURE))
                {
                    string Message = "The network connection has been lost.";
                    log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", Message);
                    if (!State.Error) TaskSQL.InsertLog(Message);
                    State.Error = true;
                }
                return false;
                //throw new Exception(string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message));
            }
        }
        private bool DeleteObject(AmazonS3Client client, string BucketName, string FilePath, out string ErrorMsg)
        {
            const string FUNCTION_NAME = "DeleteObject";
            try
            {
                string ObjectName = FileNameChangeToS3ObjectName(FilePath);
                var deleteObjectRequest = new DeleteObjectRequest
                {
                    BucketName = BucketName,
                    Key = ObjectName
                };

                //Delete object request
                var response = client.DeleteObject(deleteObjectRequest);
                log.DebugFormat("[{0}:{1}] BucketName : {2} ObjectName : {3}", CLASS_NAME, FUNCTION_NAME, BucketName, ObjectName);
                ErrorMsg = string.Empty;
                return true;
            }
            catch (AmazonS3Exception e)
            {
                ErrorMsg = string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                log.Error(ErrorMsg);
                return false;
            }
            catch (Exception e)
            {
                ErrorMsg = string.Format("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                log.Error(ErrorMsg);
                if (e.Message.Contains(CONNECTFAILURE))
                {
                    string Message = "The network connection has been lost.";
                    log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "Exception", Message);
                    if (!State.Error) TaskSQL.InsertLog(Message);
                    State.Error = true;
                }
                return false;
            }
        }

        private string GetObjectMD5(AmazonS3Client client, string BucketName, string FilePath)
        {
            const string FUNCTION_NAME = "GetObjectMD5";

            try
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                string ObjectName = FileNameChangeToS3ObjectName(FilePath);

                GetObjectMetadataRequest GetObjectMD5Request = new GetObjectMetadataRequest
                {
                    BucketName = BucketName,
                    Key = ObjectName,
                };

                var response = client.GetObjectMetadata(GetObjectMD5Request); //Get Bucket request

                string MD5Hash = response.ETag.Replace("\"", string.Empty);

                sw.Stop();
                log.DebugFormat("GetObjects({0}) {1} / {2}", BucketName, ObjectName, MD5Hash);
                return MD5Hash;
            }
            catch (AmazonS3Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}] {3}", CLASS_NAME, FUNCTION_NAME, "AmazonS3Exception", e.Message);
                return string.Empty;
            }
            catch (Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}]", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                return string.Empty;
            }
        }
        #endregion Amazon S3

        #region Analysis
        private int Analysis()
        {
            const string FUNCTION_NAME = "Analysis";
            log.InfoFormat("[{0}:{1}] Start", CLASS_NAME, FUNCTION_NAME);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            if(Job.Policy == JobData.PolicyNameList.Now) State.Filter = true;
            TaskSQL.Clear();

            //Analysis
            List<string> ExtensionList = new List<string>(Job.WhiteFileExt);
            List<string> DirectoryList = new List<string>(Job.Path);

            int FileCount = 0;
            //Directory Search
            foreach (var Directory in DirectoryList)
            {
                if (State.Quit) break;
                try { FileCount += SubDirectory(Directory, ExtensionList); } catch { };
            }

            if (Job.Policy == JobData.PolicyNameList.Now) State.Filter = false;
            sw.Stop();
            log.DebugFormat("[{0}:{1}] End. Time : {2}ms / FileCount:{3}", CLASS_NAME, FUNCTION_NAME, sw.ElapsedMilliseconds.ToString(), FileCount);
            return FileCount;
        }

        private int SubDirectory(string ParentDirectory, List<string> ExtensionList)
        {
            //const string FUNCTION_NAME = "SubDirectory";
            DirectoryInfo dInfoParent = new DirectoryInfo(ParentDirectory);

            if (!dInfoParent.Exists) return 0;

            int FileCount = 0;
            //add Folder List
            foreach (DirectoryInfo dInfo in dInfoParent.GetDirectories())
            {
                if (State.Quit) break;
                try { FileCount += SubDirectory(dInfo.FullName, ExtensionList); } catch { };
            }
            try { FileCount += AddBackupFile(ParentDirectory, ExtensionList); } catch { };

            return FileCount;
        }
        private int AddBackupFile(string ParentDirectory, List<string> ExtensionList)
        {
            //const string FUNCTION_NAME = "SubDirectory";
            //add File List
            string[] files = Directory.GetFiles(ParentDirectory);
            TaskData.TaskNameList taskName = TaskData.TaskNameList.Upload;

            int FileCount = 0;
            foreach (string file in files)    // 파일 나열
            {
                if (State.Quit) break;
                FileInfo info = new FileInfo(file);
                if (!info.Exists) continue;

                if ((info.Attributes & FileAttributes.System) == FileAttributes.System) { /*empty*/ }
                else if (ExtensionList.Contains(info.Extension.Replace(".", "").ToLower()))
                {
                    if (info.FullName.IndexOf("$") < 0)
                    {
                        TaskData item = new TaskData(taskName, info.FullName, DateTime.Now.ToString("yyyy-MM-dd-HH:mm:ss"), info.Length);
                        TaskSQL.Insert(item);
                        FileCount++;
                    }
                }
            }
            return FileCount;
        }
        #endregion Analysis

        private void InstantBackup()
        {
            const string FUNCTION_NAME = "InstantBackup";

            
            Instant = new InstantData();

            if (Job.SenderUpdate || !Instant.Analysis)
            {
                TaskSQL.InsertLog("Instant Backup Start");
                log.DebugFormat("[{0}:{1}] Instant Backup Start", CLASS_NAME, FUNCTION_NAME);

                List<ShadowCopy> ShadowCopyList = GetShadowCopies();

                Instant.Analysis = false;
                Instant.Clear();
                State.UploadClear();

                Instant.Total = Analysis();
                TaskSQL.InsertLog("Analysis File count = " + Instant.Total.ToString());
                Instant.Analysis = true;
                if (State.Quit)
                {
                    TaskSQL.Clear();
                    log.DebugFormat("[{0}:{1}] Analysis Stop!", CLASS_NAME, FUNCTION_NAME);
                    TaskSQL.InsertLog("Analysis Stop!");
                }
                else if (Instant.Total != 0) InstantRunOnce(ShadowCopyList);
                else
                {
                    log.DebugFormat("[{0}:{1}] Analysis Zero. No Backup", CLASS_NAME, FUNCTION_NAME);
                    TaskSQL.InsertLog("Analysis Zero.");
                }

                ReleaseShadowCopies(ShadowCopyList);

                State.RemainingCount = 0;
                State.RemainingSize = 0;
                Instant.Analysis = true;
                Instant.Total = 0;
                TaskSQL.Clear();

                if (State.Quit || State.Error)
                {
                    log.DebugFormat("[{0}:{1}] Backup Stop!", CLASS_NAME, FUNCTION_NAME);
                    TaskSQL.InsertLog("Backup Stop!");
                }
                else
                {
                    log.DebugFormat("[{0}:{1}] Backup Finish!", CLASS_NAME, FUNCTION_NAME);
                    TaskSQL.InsertLog("Backup Finish!");
                }
                State.Quit = true;
            }
            else if (Instant.Total != 0)
            {
                InstantRunOnce();
            }
        }
    }
}
