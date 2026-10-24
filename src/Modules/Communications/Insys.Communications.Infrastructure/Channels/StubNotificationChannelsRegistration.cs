/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using Insys.Communications.Application.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insys.Communications.Infrastructure.Channels;

public static class StubNotificationChannelsRegistration
{
    /// <summary>
    /// Registers the Wave 1 stub channels (EMAIL, SMS, PUSH, WHATSAPP; OQ-088), their outbox and test switches as
    /// singletons, and the channel registry. Calling it twice registers nothing twice.
    /// </summary>
    public static IServiceCollection AddStubNotificationChannels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<StubChannelOutbox>();
        services.TryAddSingleton<StubChannelSwitches>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, EmailChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, SmsChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, PushChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannel, WhatsAppChannel>());
        services.TryAddSingleton<INotificationChannels, NotificationChannelRegistry>();
        return services;
    }
}
