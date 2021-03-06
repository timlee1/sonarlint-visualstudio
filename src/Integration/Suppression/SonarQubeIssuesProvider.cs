﻿/*
 * SonarLint for Visual Studio
 * Copyright (C) 2016-2017 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonarQube.Client.Models;
using SonarQube.Client.Services;

namespace SonarLint.VisualStudio.Integration.Suppression
{
    public sealed class SonarQubeIssuesProvider : ISonarQubeIssuesProvider, IDisposable
    {
        private const double MillisecondsToWaitBetweenRefresh = 1000 * 60 * 1; // 1 minute

        private readonly ITimer refreshTimer;
        private readonly IActiveSolutionBoundTracker solutionBoundTacker;
        private readonly ISonarQubeService sonarQubeService;

        private IList<SonarQubeIssue> cachedSuppressedIssues;
        private bool isDisposed;
        private CancellationTokenSource cancellationTokenSource;

        public SonarQubeIssuesProvider(ISonarQubeService sonarQubeService, 
            IActiveSolutionBoundTracker solutionBoundTracker,
            ITimerFactory timerFactory)
        {
            if (sonarQubeService == null)
            {
                throw new ArgumentNullException(nameof(sonarQubeService));
            }
            if (solutionBoundTracker == null)
            {
                throw new ArgumentNullException(nameof(solutionBoundTracker));
            }
            if (timerFactory == null)
            {
                throw new ArgumentNullException(nameof(timerFactory));
            }

            this.sonarQubeService = sonarQubeService;
            this.solutionBoundTacker = solutionBoundTracker;
            this.solutionBoundTacker.SolutionBindingChanged += OnSolutionBoundChanged;

            refreshTimer = timerFactory.Create();
            refreshTimer.AutoReset = true;
            refreshTimer.Interval = MillisecondsToWaitBetweenRefresh;
            refreshTimer.Elapsed += OnRefreshTimerElapsed;

            if (this.solutionBoundTacker.IsActiveSolutionBound)
            {
                SynchronizeSuppressedIssues();
                refreshTimer.Start();
            }
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            refreshTimer.Dispose();
            this.solutionBoundTacker.SolutionBindingChanged -= OnSolutionBoundChanged;
            this.isDisposed = true;
        }

        public IEnumerable<SonarQubeIssue> GetSuppressedIssues(string projectGuid, string filePath)
        {
            // TODO: Block the call while the cache is being built + handle multi-threading

            // TODO: ensure we've got data to enable end to end testing
            if (solutionBoundTacker.IsActiveSolutionBound &&
                this.cachedSuppressedIssues == null)
            {
                SynchronizeSuppressedIssues().Wait(30000);
            }

            if (this.cachedSuppressedIssues == null)
            {
                return Enumerable.Empty<SonarQubeIssue>();
            }

            return this.cachedSuppressedIssues.Where(x =>
                x.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
                x.ModuleKey.Equals(BuildModuleKey(projectGuid), StringComparison.OrdinalIgnoreCase));
        }

        private string BuildModuleKey(string projectGuid)
        {
            return $"{solutionBoundTacker.ProjectKey}:{solutionBoundTacker.ProjectKey}:{projectGuid}";
        }

        private async void OnRefreshTimerElapsed(object sender, TimerEventArgs e)
        {
            await SynchronizeSuppressedIssues();
        }

        private async void OnSolutionBoundChanged(object sender, ActiveSolutionBindingEventArgs eventArgs)
        {
            if (solutionBoundTacker.IsActiveSolutionBound)
            {
                await SynchronizeSuppressedIssues();
                refreshTimer.Start();
            }
            else
            {
                cachedSuppressedIssues = null;
                refreshTimer.Stop();
            }
        }

        private async Task SynchronizeSuppressedIssues()
        {
            // TODO: may not be connected
            if (!this.sonarQubeService.IsConnected)
            {
                return;
            }

            cancellationTokenSource?.Cancel();
            cancellationTokenSource = new CancellationTokenSource();

            // TODO: Handle race conditions
            this.cachedSuppressedIssues = await this.sonarQubeService.GetSuppressedIssuesAsync(
                this.solutionBoundTacker.ProjectKey, cancellationTokenSource.Token);
        }
    }
}
