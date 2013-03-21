// 
// Author: Nate Kohari <nate@enkari.com>
// Copyright (c) 2007-2010, Enkari, Ltd.
// 
// Dual-licensed under the Apache License, Version 2.0, and the Microsoft Public License (Ms-PL).
// See the file LICENSE.txt for details.
// 

namespace Ninject
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Ninject.Activation;
    using Ninject.Activation.Blocks;
    using Ninject.Activation.Caching;
    using Ninject.Components;
    using Ninject.Infrastructure;
    using Ninject.Infrastructure.Introspection;
    using Ninject.Infrastructure.Language;
    using Ninject.Modules;
    using Ninject.Parameters;
    using Ninject.Planning;
    using Ninject.Planning.Bindings;
    using Ninject.Planning.Bindings.Resolvers;
    using Ninject.Syntax;

    /// <summary>
    /// The base implementation of an <see cref="IKernel"/>.
    /// </summary>
    public abstract class KernelBase : BindingRoot, IKernel
    {
        /// <summary>
        /// Lock used when adding missing bindings.
        /// </summary>
        protected readonly object HandleMissingBindingLockObject = new object();        
        
        private readonly Multimap<Type, IBinding> bindings = new Multimap<Type, IBinding>();
        private readonly Multimap<Type, IBinding> bindingCache = new Multimap<Type, IBinding>();
        private readonly Dictionary<string, INinjectModule> modules = new Dictionary<string, INinjectModule>();
        private readonly BindingPrecedenceComparer bindingPrecedenceComparer = new BindingPrecedenceComparer();

        private ICache cache;
        private IPlanner planner;
        private IPipeline pipeline;
        private IEnumerable<IMissingBindingResolver> missingBindingResolvers;
        private IEnumerable<IBindingResolver> bindingResolvers;

        /// <summary>
        /// Initializes a new instance of the <see cref="KernelBase"/> class.
        /// </summary>
        protected KernelBase()
            : this(new ComponentContainer(), new NinjectSettings(), new INinjectModule[0])
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="KernelBase"/> class.
        /// </summary>
        /// <param name="modules">The modules to load into the kernel.</param>
        protected KernelBase(params INinjectModule[] modules)
            : this(new ComponentContainer(), new NinjectSettings(), modules)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="KernelBase"/> class.
        /// </summary>
        /// <param name="settings">The configuration to use.</param>
        /// <param name="modules">The modules to load into the kernel.</param>
        protected KernelBase(INinjectSettings settings, params INinjectModule[] modules)
            : this(new ComponentContainer(), settings, modules)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="KernelBase"/> class.
        /// </summary>
        /// <param name="components">The component container to use.</param>
        /// <param name="settings">The configuration to use.</param>
        /// <param name="modules">The modules to load into the kernel.</param>
        protected KernelBase(IComponentContainer components, INinjectSettings settings, params INinjectModule[] modules)
        {
            Ensure.ArgumentNotNull(settings, "settings");
            Ensure.ArgumentNotNull(modules, "modules");

            this.Settings = settings;

            this.Components = components;
            components.Kernel = this;

            this.AddComponents();

            this.Bind<IKernel>().ToConstant(this).InTransientScope();
            this.Bind<IResolutionRoot>().ToConstant(this).InTransientScope();

#if !NO_ASSEMBLY_SCANNING
            if (this.Settings.LoadExtensions)
            {
                this.Load(this.Settings.ExtensionSearchPatterns);
            }
#endif
            this.Load(modules);
        }

        /// <summary>
        /// Gets the kernel settings.
        /// </summary>
        public INinjectSettings Settings { get; private set; }

        /// <summary>
        /// Gets the component container, which holds components that contribute to Ninject.
        /// </summary>
        public IComponentContainer Components { get; private set; }

        /// <summary>
        /// Releases resources held by the object.
        /// </summary>
        public override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                if (this.Components != null)
                {
                    // Deactivate all cached instances before shutting down the kernel.
                    this.Cache.Clear();

                    this.Components.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Gets the cache.
        /// </summary>
        /// <value>The cache.</value>
        public ICache Cache
        {
            get
            {
                return this.cache ?? (this.cache = this.Components.Get<ICache>());
            }
        }

        /// <summary>
        /// Gets the planner.
        /// </summary>
        /// <value>The planner.</value>
        public IPlanner Planner
        {
            get
            {
                return this.planner ?? (this.planner = this.Components.Get<IPlanner>());
            }
        }

        /// <summary>
        /// Gets the pipeline.
        /// </summary>
        /// <value>The pipeline.</value>
        public IPipeline Pipeline
        {
            get
            {
                return this.pipeline ?? (this.pipeline = this.Components.Get<IPipeline>());
            }
        }

        /// <summary>
        /// Gets the binding resolvers.
        /// </summary>
        /// <value>The binding resolvers.</value>
        public IEnumerable<IBindingResolver> BindingResolvers
        {
            get
            {
                return this.bindingResolvers ?? (this.bindingResolvers = this.Components.GetAll<IBindingResolver>());
            }
        }

        /// <summary>
        /// Gets the missing binding resolvers.
        /// </summary>
        /// <value>The missing binding resolvers.</value>
        public IEnumerable<IMissingBindingResolver> MissingBindingResolvers
        {
            get
            {
                return this.missingBindingResolvers ?? (this.missingBindingResolvers = this.Components.GetAll<IMissingBindingResolver>());
            }
        }
        
        /// <summary>
        /// Unregisters all bindings for the specified service.
        /// </summary>
        /// <param name="service">The service to unbind.</param>
        public override void Unbind(Type service)
        {
            Ensure.ArgumentNotNull(service, "service");

            this.bindings.RemoveAll(service);

            lock (this.bindingCache)
            {
                this.bindingCache.Clear();
            }
        }

        /// <summary>
        /// Registers the specified binding.
        /// </summary>
        /// <param name="binding">The binding to add.</param>
        public override void AddBinding(IBinding binding)
        {
            Ensure.ArgumentNotNull(binding, "binding");

            this.AddBindings(new[] { binding });
        }

        /// <summary>
        /// Unregisters the specified binding.
        /// </summary>
        /// <param name="binding">The binding to remove.</param>
        public override void RemoveBinding(IBinding binding)
        {
            Ensure.ArgumentNotNull(binding, "binding");

            this.bindings.Remove(binding.Service, binding);

            lock (this.bindingCache)
                this.bindingCache.Clear();
        }

        /// <summary>
        /// Determines whether a module with the specified name has been loaded in the kernel.
        /// </summary>
        /// <param name="name">The name of the module.</param>
        /// <returns><c>True</c> if the specified module has been loaded; otherwise, <c>false</c>.</returns>
        public bool HasModule(string name)
        {
            Ensure.ArgumentNotNullOrEmpty(name, "name");
            return this.modules.ContainsKey(name);
        }

        /// <summary>
        /// Gets the modules that have been loaded into the kernel.
        /// </summary>
        /// <returns>A series of loaded modules.</returns>
        public IEnumerable<INinjectModule> GetModules()
        {
            return this.modules.Values.ToArray();
        }

        /// <summary>
        /// Loads the module(s) into the kernel.
        /// </summary>
        /// <param name="m">The modules to load.</param>
        public void Load(IEnumerable<INinjectModule> m)
        {
            Ensure.ArgumentNotNull(m, "modules");

            m = m.ToList();
            foreach (INinjectModule module in m)
            {
                if (string.IsNullOrEmpty(module.Name))
                {
                    throw new NotSupportedException(ExceptionFormatter.ModulesWithNullOrEmptyNamesAreNotSupported());
                }
                
                INinjectModule existingModule;

                if (this.modules.TryGetValue(module.Name, out existingModule))
                {
                    throw new NotSupportedException(ExceptionFormatter.ModuleWithSameNameIsAlreadyLoaded(module, existingModule));
                }

                module.OnLoad(this);

                this.modules.Add(module.Name, module);
            }

            foreach (INinjectModule module in m)
            {
                module.OnVerifyRequiredModules();
            }
        }

#if !NO_ASSEMBLY_SCANNING
        /// <summary>
        /// Loads modules from the files that match the specified pattern(s).
        /// </summary>
        /// <param name="filePatterns">The file patterns (i.e. "*.dll", "modules/*.rb") to match.</param>
        public void Load(IEnumerable<string> filePatterns)
        {
            var moduleLoader = this.Components.Get<IModuleLoader>();
            moduleLoader.LoadModules(filePatterns);
        }

        /// <summary>
        /// Loads modules defined in the specified assemblies.
        /// </summary>
        /// <param name="assemblies">The assemblies to search.</param>
        public void Load(IEnumerable<Assembly> assemblies)
        {
            this.Load(assemblies.SelectMany(asm => asm.GetNinjectModules()));
        }
#endif //!NO_ASSEMBLY_SCANNING

        /// <summary>
        /// Unloads the plugin with the specified name.
        /// </summary>
        /// <param name="name">The plugin's name.</param>
        public void Unload(string name)
        {
            Ensure.ArgumentNotNullOrEmpty(name, "name");

            INinjectModule module;

            if (!this.modules.TryGetValue(name, out module))
            {
                throw new NotSupportedException(ExceptionFormatter.NoModuleLoadedWithTheSpecifiedName(name));
            }

            module.OnUnload(this);

            this.modules.Remove(name);
        }

        /// <summary>
        /// Injects the specified existing instance, without managing its lifecycle.
        /// </summary>
        /// <param name="instance">The instance to inject.</param>
        /// <param name="parameters">The parameters to pass to the request.</param>
        public virtual void Inject(object instance, params IParameter[] parameters)
        {
            Ensure.ArgumentNotNull(instance, "instance");
            Ensure.ArgumentNotNull(parameters, "parameters");

            Type service = instance.GetType();

            var binding = new Binding(service);
            var request = this.CreateRequest(service, null, parameters, false, false);
            var context = this.CreateContext(request, binding);

            context.Plan = this.Planner.GetPlan(service);

            var reference = new InstanceReference { Instance = instance };
            this.Pipeline.Activate(context, reference);
        }

        /// <summary>
        /// Deactivates and releases the specified instance if it is currently managed by Ninject.
        /// </summary>
        /// <param name="instance">The instance to release.</param>
        /// <returns><see langword="True"/> if the instance was found and released; otherwise <see langword="false"/>.</returns>
        public virtual bool Release(object instance)
        {
            Ensure.ArgumentNotNull(instance, "instance");
            return this.Cache.Release(instance);
        }

        /// <summary>
        /// Determines whether the specified request can be resolved.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns><c>True</c> if the request can be resolved; otherwise, <c>false</c>.</returns>
        public virtual bool CanResolve(IRequest request)
        {
            Ensure.ArgumentNotNull(request, "request");
            return this.GetBindings(request.Service).Any(b => this.SatifiesRequest(b, request));
        }

        /// <summary>
        /// Determines whether the specified request can be resolved.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="ignoreImplicitBindings">if set to <c>true</c> implicit bindings are ignored.</param>
        /// <returns>
        ///     <c>True</c> if the request can be resolved; otherwise, <c>false</c>.
        /// </returns>
        public virtual bool CanResolve(IRequest request, bool ignoreImplicitBindings)
        {
            Ensure.ArgumentNotNull(request, "request");
            return this.GetBindings(request.Service)
                .Any(binding => (!ignoreImplicitBindings || !binding.IsImplicit) && this.SatifiesRequest(binding, request));
        }

        /// <summary>
        /// Resolves instances for the specified request. The instances are not actually resolved
        /// until a consumer iterates over the enumerator.
        /// </summary>
        /// <param name="request">The request to resolve.</param>
        /// <returns>An enumerator of instances that match the request.</returns>
        public virtual IEnumerable<object> Resolve(IRequest request)
        {
            Ensure.ArgumentNotNull(request, "request");
            return Resolve(request, true);
        }

        private IEnumerable<object> Resolve(IRequest request, bool handleMissingBindings)
        {
            var resolveBindings = this.GetBindings(request.Service).Where(b => this.SatifiesRequest(b, request));
            var resolveBindingsIterator = resolveBindings.GetEnumerator();
            if (!resolveBindingsIterator.MoveNext())
            {
                return this.ResolveWithMissingBindings(request, handleMissingBindings);
            }

            if (request.IsUnique)
            {
                var first = resolveBindingsIterator.Current;
                if (resolveBindingsIterator.MoveNext() &&
                    this.bindingPrecedenceComparer.Compare(first, resolveBindingsIterator.Current) == 0)
                {
                    if (request.IsOptional)
                    {
                        return Enumerable.Empty<object>();
                    }

                    throw new ActivationException(ExceptionFormatter.CouldNotUniquelyResolveBinding(request));
                }

                return new object[] { this.CreateContext(request, first).Resolve() };
            }
            else
            {
                if (resolveBindings.Any(binding => !binding.IsImplicit))
                {
                    resolveBindings = resolveBindings.Where(binding => !binding.IsImplicit);
                }
            }

            return resolveBindings
                .Select(binding => this.CreateContext(request, binding).Resolve());
        }
        
        private IEnumerable<object> Resolve_(IRequest request, bool handleMissingBindings)
        {
            var resolveBindings = this.GetBindings(request.Service).Where(b => this.SatifiesRequest(b, request));
            var resolveBindingsArray = resolveBindings.ToArray();
            if (resolveBindingsArray.Length == 0)
            {
                return this.ResolveWithMissingBindings(request, handleMissingBindings);
            }

            if (request.IsUnique)
            {
                if (resolveBindingsArray.Length > 1 && 
                    this.bindingPrecedenceComparer.Compare(resolveBindingsArray[0], resolveBindingsArray[1]) == 0)
                {
                    if (request.IsOptional)
                    {
                        return Enumerable.Empty<object>();
                    }

                    throw new ActivationException(ExceptionFormatter.CouldNotUniquelyResolveBinding(request));
                }

                resolveBindings = resolveBindingsArray.Take(1);
            }
            else
            {
                if (resolveBindingsArray.Any(binding => !binding.IsImplicit))
                {
                    resolveBindings = resolveBindingsArray.Where(binding => !binding.IsImplicit);
                }      
                else
                {
                    resolveBindings = resolveBindingsArray;
                }
            }

            return resolveBindings
                .Select(binding => this.CreateContext(request, binding).Resolve());
        }

        private IEnumerable<object> ResolveWithMissingBindings(IRequest request, bool handleMissingBindings)
        {
            if (handleMissingBindings)
            {
                if (this.HandleMissingBinding(request))
                {
                    return this.Resolve(request, false);
                }
            }

            if (request.IsOptional)
            {
                return Enumerable.Empty<object>();
            }

            throw new ActivationException(ExceptionFormatter.CouldNotResolveBinding(request));
        }


        /// <summary>
        /// Creates a request for the specified service.
        /// </summary>
        /// <param name="service">The service that is being requested.</param>
        /// <param name="constraint">The constraint to apply to the bindings to determine if they match the request.</param>
        /// <param name="parameters">The parameters to pass to the resolution.</param>
        /// <param name="isOptional"><c>True</c> if the request is optional; otherwise, <c>false</c>.</param>
        /// <param name="isUnique"><c>True</c> if the request should return a unique result; otherwise, <c>false</c>.</param>
        /// <returns>The created request.</returns>
        public virtual IRequest CreateRequest(Type service, Func<IBindingMetadata, bool> constraint, IEnumerable<IParameter> parameters, bool isOptional, bool isUnique)
        {
            Ensure.ArgumentNotNull(service, "service");
            Ensure.ArgumentNotNull(parameters, "parameters");

            return new Request(service, constraint, parameters, null, isOptional, isUnique);
        }

        /// <summary>
        /// Begins a new activation block, which can be used to deterministically dispose resolved instances.
        /// </summary>
        /// <returns>The new activation block.</returns>
        public virtual IActivationBlock BeginBlock()
        {
            return new ActivationBlock(this);
        }

        /// <summary>
        /// Gets the bindings registered for the specified service.
        /// </summary>
        /// <param name="service">The service in question.</param>
        /// <returns>A series of bindings that are registered for the service.</returns>
        public virtual IEnumerable<IBinding> GetBindings(Type service)
        {
            Ensure.ArgumentNotNull(service, "service");

            lock (this.bindingCache)
            {
                ICollection<IBinding> result;
                if (this.bindingCache.TryGet(service, out result))
                {
                    return result;
                }

                var bindingPrecedenceComparer = this.GetBindingPrecedenceComparer();
                this.BindingResolvers
                    .SelectMany(resolver => resolver.Resolve(this.bindings, service))
                    .OrderByDescending(b => b, bindingPrecedenceComparer)
                    .Map(binding => this.bindingCache.Add(service, binding));

                return this.bindingCache[service];
            }
        }

        /// <summary>
        /// Returns an IComparer that is used to determine resolution precedence.
        /// </summary>
        /// <returns>An IComparer that is used to determine resolution precedence.</returns>
        protected virtual IComparer<IBinding> GetBindingPrecedenceComparer()
        {
            return this.bindingPrecedenceComparer;
        }

        /// <summary>
        /// Returns a predicate that can determine if a given IBinding matches the request.
        /// </summary>
        /// <param name="binding">the binding</param>
        /// <param name="request">The request/</param>
        /// <returns>A predicate that can determine if a given IBinding matches the request.</returns>
        protected virtual bool SatifiesRequest(IBinding binding, IRequest request)
        {
            return binding.Matches(request) && request.Matches(binding);
        }

        /// <summary>
        /// Adds components to the kernel during startup.
        /// </summary>
        protected abstract void AddComponents();

        /// <summary>
        /// Attempts to handle a missing binding for a service.
        /// </summary>
        /// <param name="service">The service.</param>
        /// <returns><c>True</c> if the missing binding can be handled; otherwise <c>false</c>.</returns>
        [Obsolete]
        protected virtual bool HandleMissingBinding(Type service)
        {
            return false;
        }

        /// <summary>
        /// Attempts to handle a missing binding for a request.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns><c>True</c> if the missing binding can be handled; otherwise <c>false</c>.</returns>
        protected virtual bool HandleMissingBinding(IRequest request)
        {
#pragma warning disable 612,618
            if (this.HandleMissingBinding(request.Service))
            {
                return true;
            }
#pragma warning restore 612,618

            var components = this.MissingBindingResolvers;
            
            // Take the first set of bindings that resolve.
            var bindings = components
                .Select(c => c.Resolve(this.bindings, request).ToList())
                .FirstOrDefault(b => b.Any());

            if (bindings == null)
            {
                return false;
            }

            lock (this.HandleMissingBindingLockObject)
            {
                if (!this.CanResolve(request))
                {
                    bindings.Map(binding => binding.IsImplicit = true);
                    this.AddBindings(bindings);
                }
            }

            return true;
        }

        /// <summary>
        /// Returns a value indicating whether the specified service is self-bindable.
        /// </summary>
        /// <param name="service">The service.</param>
        /// <returns><see langword="True"/> if the type is self-bindable; otherwise <see langword="false"/>.</returns>
        [Obsolete]
        protected virtual bool TypeIsSelfBindable(Type service)
        {
            return !service.IsInterface
                && !service.IsAbstract
                && !service.IsValueType
                && service != typeof(string)
                && !service.ContainsGenericParameters;
        }

        /// <summary>
        /// Creates a context for the specified request and binding.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="binding">The binding.</param>
        /// <returns>The created context.</returns>
        protected virtual IContext CreateContext(IRequest request, IBinding binding)
        {
            return new Context(this, request, binding);
        }

        private void AddBindings(IEnumerable<IBinding> bindings)
        {
            bindings.Map(binding => this.bindings.Add(binding.Service, binding));

            lock (this.bindingCache)
                this.bindingCache.Clear();
        }

        object IServiceProvider.GetService(Type service)
        {
            return this.Get(service);
        }

        private class BindingPrecedenceComparer : IComparer<IBinding>
        {
            public int Compare(IBinding x, IBinding y)
            {
                if (x == y)
                {
                    return 0;
                }

                // Each function represents a level of precedence.
                var funcs = new List<Func<IBinding, bool>>
                            {
                                b => b != null,       // null bindings should never happen, but just in case
                                b => b.IsConditional, // conditional bindings > unconditional
                                b => !b.Service.ContainsGenericParameters, // closed generics > open generics
                                b => !b.IsImplicit,   // explicit bindings > implicit
                            };

                var q = from func in funcs
                        let xVal = func(x)
                        where xVal != func(y) 
                        select xVal ? 1 : -1;

                // returns the value of the first function that represents a difference
                // between the bindings, or else returns 0 (equal)
                return q.FirstOrDefault();
            }
        }
    }
}