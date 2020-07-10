﻿using Microsoft.EntityFrameworkCore;
using ReconNess.Core;
using ReconNess.Core.Models;
using ReconNess.Core.Services;
using ReconNess.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReconNess.Services
{
    /// <summary>
    /// This class implement <see cref="ISubdomainService"/> 
    /// </summary>
    public class SubdomainService : Service<Subdomain>, IService<Subdomain>, ISubdomainService
    {
        private readonly ILabelService labelService;
        private readonly INotificationService notificationService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ISubdomainService" /> class
        /// </summary>
        /// <param name="unitOfWork"><see cref="IUnitOfWork"/></param>
        /// <param name="labelService"><see cref="ILabelService"/></param>
        /// <param name="notificationService"><see cref="INotificationService"/></param>
        public SubdomainService(
            IUnitOfWork unitOfWork,
            ILabelService labelService,
            INotificationService notificationService)
            : base(unitOfWork)
        {
            this.labelService = labelService;
            this.notificationService = notificationService;
        }

        /// <summary>
        /// <see cref="ISubdomainService.GetSubdomainsByTargetAsync(RootDomain, CancellationToken)"/>
        /// </summary>
        public async Task<List<Subdomain>> GetSubdomainsByTargetAsync(RootDomain rootDomain, CancellationToken cancellationToken = default)
        {
            return await this.GetAllQueryableByCriteria(s => s.RootDomain == rootDomain, cancellationToken)
                .Include(t => t.Services)
                .Include(t => t.Notes)
                .Include(t => t.Labels)
                    .ThenInclude(ac => ac.Label)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// <see cref="ISubdomainService.UpdateSubdomainAsync(Subdomain, Agent, ScriptOutput, bool, CancellationToken)"/>
        /// </summary>
        public async Task UpdateSubdomain(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            await this.UpdateSubdomainIpAddress(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            await this.UpdateSubdomainIsAlive(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            await this.UpdateSubdomainHasHttpOpen(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            await this.UpdateSubdomainTakeover(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            await this.UpdateSubdomainDirectory(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            await this.UpdateSubdomainService(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            await this.UpdateSubdomainNote(subdomain, agent, scriptOutput, activateNotification, cancellationToken);

            await this.UpdateSubdomainLabel(subdomain, agent, scriptOutput, activateNotification, cancellationToken);
            this.UpdateSubdomainAgent(subdomain, agent, activateNotification, cancellationToken);
            this.UpdateSubdomainScreenshot(subdomain, agent, scriptOutput, activateNotification, cancellationToken);

            this.UnitOfWork.Repository<Subdomain>().Update(subdomain);
        }

        /// <summary>
        /// <see cref="ISubdomainService.DeleteSubdomainAsync(Subdomain, CancellationToken)"/>
        /// </summary>
        public async Task DeleteSubdomainAsync(Subdomain subdomain, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                this.UnitOfWork.BeginTransaction(cancellationToken);

                if (subdomain.Notes != null)
                {
                    this.UnitOfWork.Repository<Note>().Delete(subdomain.Notes, cancellationToken);
                }

                this.UnitOfWork.Repository<Service>().DeleteRange(subdomain.Services.ToList(), cancellationToken);
                this.UnitOfWork.Repository<Subdomain>().Delete(subdomain, cancellationToken);

                await this.UnitOfWork.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                this.UnitOfWork.Rollback(cancellationToken);
                throw ex;
            }
        }

        /// <summary>
        /// <see cref="ISubdomainService.DeleteSubdomains(ICollection{Subdomain}, CancellationToken)"/>
        /// </summary>
        public void DeleteSubdomains(ICollection<Subdomain> subdomains, CancellationToken cancellationToken = default)
        {
            foreach (var subdomain in subdomains)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (subdomain.Notes != null)
                {
                    this.UnitOfWork.Repository<Note>().Delete(subdomain.Notes, cancellationToken);
                }

                this.UnitOfWork.Repository<Service>().DeleteRange(subdomain.Services.ToList(), cancellationToken);
                this.UnitOfWork.Repository<Subdomain>().Delete(subdomain, cancellationToken);
            }
        }

        /// <summary>
        /// Assign Ip address to the subdomain
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainIpAddress(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (scriptOutput.Ip != null && Helpers.Helpers.ValidateIPv4(scriptOutput.Ip) && subdomain.IpAddress != scriptOutput.Ip)
            {
                subdomain.IpAddress = scriptOutput.Ip;

                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.IpAddressPayload))
                {
                    var payload = agent.AgentNotification.IpAddressPayload.Replace("{{domain}}", subdomain.Name).Replace("{{ip}}", scriptOutput.Ip);
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain if it has http port open
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainHasHttpOpen(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (scriptOutput.HasHttpOpen != null && subdomain.HasHttpOpen != scriptOutput.HasHttpOpen.Value)
            {
                subdomain.HasHttpOpen = scriptOutput.HasHttpOpen.Value;

                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.HasHttpOpenPayload))
                {
                    var payload = agent.AgentNotification.HasHttpOpenPayload.Replace("{{domain}}", subdomain.Name).Replace("{{httpOpen}}", scriptOutput.HasHttpOpen.Value.ToString());
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain if it can be takeover
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainTakeover(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (scriptOutput.Takeover != null && subdomain.Takeover != scriptOutput.Takeover.Value)
            {
                subdomain.Takeover = scriptOutput.Takeover.Value;

                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.TakeoverPayload))
                {
                    var payload = agent.AgentNotification.TakeoverPayload.Replace("{{domain}}", subdomain.Name).Replace("{{takeover}}", scriptOutput.Takeover.Value.ToString());
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain with screenshots
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        private void UpdateSubdomainScreenshot(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(scriptOutput.HttpScreenshotFilePath))
            {
                try
                {
                    var fileBase64 = Convert.ToBase64String(File.ReadAllBytes(scriptOutput.HttpScreenshotFilePath));
                    if (subdomain.ServiceHttp == null)
                    {
                        subdomain.ServiceHttp = new ServiceHttp();
                    }

                    subdomain.ServiceHttp.ScreenshotHttpPNGBase64 = fileBase64;
                }
                catch
                {

                }
            }

            if (string.IsNullOrEmpty(scriptOutput.HttpsScreenshotFilePath))
            {
                try
                {
                    var fileBase64 = Convert.ToBase64String(File.ReadAllBytes(scriptOutput.HttpsScreenshotFilePath));
                    if (subdomain.ServiceHttp == null)
                    {
                        subdomain.ServiceHttp = new ServiceHttp();
                    }

                    subdomain.ServiceHttp.ScreenshotHttpsPNGBase64 = fileBase64;
                }
                catch
                {

                }
            }
        }

        /// <summary>
        /// Update the subdomain with directory discovery
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainDirectory(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(scriptOutput.HttpDirectory))
            {
                var httpDirectory = scriptOutput.HttpDirectory.TrimEnd('/').TrimEnd();
                if (subdomain.ServiceHttp == null)
                {
                    subdomain.ServiceHttp = new ServiceHttp();
                }

                if (subdomain.ServiceHttp.Directories == null)
                {
                    subdomain.ServiceHttp.Directories = new List<ServiceHttpDirectory>();
                }

                if (subdomain.ServiceHttp.Directories.Any(d => d.Directory == httpDirectory))
                {
                    return;
                }

                var directory = new ServiceHttpDirectory()
                {
                    Directory = httpDirectory,
                    StatusCode = scriptOutput.HttpDirectoryStatusCode,
                    Method = scriptOutput.HttpDirectoryMethod,
                    Size = scriptOutput.HttpDirectorySize
                };

                subdomain.ServiceHttp.Directories.Add(directory);

                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.DirectoryPayload))
                {
                    var payload = agent.AgentNotification.DirectoryPayload.Replace("{{domain}}", subdomain.Name).Replace("{{directory}}", httpDirectory);
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain if is a new service with open port
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainService(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scriptOutput.Service))
            {
                return;
            }

            var service = new Service
            {
                Name = scriptOutput.Service.ToLower(),
                Port = scriptOutput.Port.Value
            };

            if (subdomain.Services == null)
            {
                subdomain.Services = new List<Service>();
            }

            if (!subdomain.Services.Any(s => s.Name == service.Name && s.Port == service.Port))
            {
                subdomain.Services.Add(service);

                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.ServicePayload))
                {
                    var payload = agent.AgentNotification.ServicePayload.Replace("{{domain}}", subdomain.Name).Replace("{{service}}", service.Name).Replace("{{port}}", service.Port.ToString());
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain Note
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainNote(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(scriptOutput.Note))
            {
                if (subdomain.Notes == null)
                {
                    subdomain.Notes = new Note();
                }

                subdomain.Notes.Notes = subdomain.Notes.Notes + '\n' + scriptOutput.Note;
                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.NotePayload))
                {
                    var payload = agent.AgentNotification.NotePayload.Replace("{{domain}}", subdomain.Name).Replace("{{note}}", scriptOutput.Note);
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain if is Alive
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainIsAlive(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (scriptOutput.IsAlive != null && subdomain.IsAlive != scriptOutput.IsAlive)
            {
                subdomain.IsAlive = scriptOutput.IsAlive.Value;

                if (activateNotification && agent.NotifyNewFound && agent.AgentNotification != null && !string.IsNullOrEmpty(agent.AgentNotification.IsAlivePayload))
                {
                    var payload = agent.AgentNotification.IsAlivePayload.Replace("{{domain}}", subdomain.Name).Replace("{{isAlive}}", scriptOutput.IsAlive.Value.ToString());
                    await this.notificationService.SendAsync(payload, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Update the subdomain label
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The Agent</param>
        /// <param name="scriptOutput">The terminal output one line</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        /// <returns>A task</returns>
        private async Task UpdateSubdomainLabel(Subdomain subdomain, Agent agent, ScriptOutput scriptOutput, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(scriptOutput.Label) &&
                !subdomain.Labels.Any(l => scriptOutput.Label.Equals(l.Label.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var label = await this.labelService.GetByCriteriaAsync(l => l.Name.ToLower() == scriptOutput.Label.ToLower());
                if (label == null)
                {
                    var random = new Random();
                    label = new Label
                    {
                        Name = scriptOutput.Label,
                        Color = string.Format("#{0:X6}", random.Next(0x1000000))
                    };
                }

                subdomain.Labels.Add(new SubdomainLabel
                {
                    Label = label,
                    SubdomainId = subdomain.Id
                });
            }
        }

        /// <summary>
        /// Update the subdomain agent property with the agent name if was updated before
        /// </summary>
        /// <param name="subdomain">The subdomain</param>
        /// <param name="agent">The agent</param>
        /// <param name="activateNotification">If the notification is active</param>
        /// <param name="cancellationToken">Notification that operations should be canceled</param>
        private void UpdateSubdomainAgent(Subdomain subdomain, Agent agent, bool activateNotification, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(subdomain.FromAgents))
            {
                subdomain.FromAgents = agent.Name;
            }
            else if (!subdomain.FromAgents.Contains(agent.Name))
            {
                subdomain.FromAgents = string.Join(", ", subdomain.FromAgents, agent.Name);
            }
        }
    }
}
