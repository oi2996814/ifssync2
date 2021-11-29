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
using log4net;
using log4net.Config;
using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using IfsSync2Data;
using Microsoft.Win32;

[assembly: XmlConfigurator(ConfigFile = "IfsSync2TrayIconLigConfig.xml", Watch = true)]

namespace IfsSync2TrayIcon
{
    class Program
    {
        private static readonly string CLASS_NAME = "TrayIcon";

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly TrayIconConfig TrayIconConfigs = new TrayIconConfig(true);

        static void Main()
        {
            const string FUNCTION_NAME = "Init";

            Mutex mutex = new Mutex(true, MainData.MUTEX_NAME_TRAYICON, out bool CreateNew);

            if(!CreateNew)
            {
                log.ErrorFormat("[{0}:{1}:{2}] Prevent duplicate execution", CLASS_NAME, FUNCTION_NAME, "Mutex");
                return;
            }
            MainUtility.DeleteOldLogs(MainData.GetLogFolder("TrayIcon"));

            TrayIconManager TrayIcon = new TrayIconManager();

            while (true)
            {
                try
                {
                    if (TrayIcon.SetTray()) break;
                    Delay(TrayIconConfigs.Delay);
                }
                catch (Exception e)
                {
                    log.ErrorFormat("[{0}:{1}:{2}] SetTray Failed : {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                }
            }

            while (true)
            {
                try { TrayIcon.UpdateTray(); }
                catch(Exception e)
                { 
                    log.ErrorFormat("[{0}:{1}:{2}] UpdateTray Failed : {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
                    break;
                }
                Delay(TrayIconConfigs.Delay);
            }

            try { TrayIcon.Close(); }
            catch (Exception e)
            {
                log.ErrorFormat("[{0}:{1}:{2}] Close Failed : {3}", CLASS_NAME, FUNCTION_NAME, "Exception", e.Message);
            }
            
        }

        private static DateTime Delay(int MS)
        {

            DateTime ThisMoment = DateTime.Now;
            TimeSpan duration = new TimeSpan(0, 0, 0, 0, MS);
            DateTime AfterWards = ThisMoment.Add(duration);
            while (AfterWards >= ThisMoment)
            {
                Application.DoEvents();
                ThisMoment = DateTime.Now;
                Thread.Sleep(100);
            }
            return DateTime.Now;
        }

    }
}
