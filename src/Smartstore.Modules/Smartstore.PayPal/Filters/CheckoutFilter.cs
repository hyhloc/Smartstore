﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Smartstore.Core;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Data;
using Smartstore.Core.Widgets;
using Smartstore.PayPal.Components;
using Smartstore.PayPal.Services;
using Smartstore.Web.Controllers;
using Smartstore.Web.Models.Checkout;

namespace Smartstore.PayPal.Filters
{
    public class CheckoutFilter : IAsyncResultFilter
    {
        private static readonly string[] _apms =
        {
            PayPalConstants.Giropay,
            PayPalConstants.Sofort,
            PayPalConstants.Bancontact,
            PayPalConstants.Blik,
            PayPalConstants.Eps,
            PayPalConstants.Ideal,
            PayPalConstants.MyBank,
            PayPalConstants.Przelewy24
        };

        private readonly SmartDbContext _db;
        private readonly ICommonServices _services;
        private readonly PayPalSettings _settings;
        private readonly ICheckoutStateAccessor _checkoutStateAccessor;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly Lazy<IWidgetProvider> _widgetProvider;
        private readonly PayPalHelper _payPalHelper;
        
        public CheckoutFilter(
            SmartDbContext db,
            ICommonServices services,
            PayPalSettings settings,
            ICheckoutStateAccessor checkoutStateAccessor,
            IHttpContextAccessor httpContextAccessor,
            Lazy<IWidgetProvider> widgetProvider,
            PayPalHelper payPalHelper)
        {
            _db = db;
            _services = services;
            _settings = settings;
            _checkoutStateAccessor = checkoutStateAccessor;
            _httpContextAccessor = httpContextAccessor;
            _widgetProvider = widgetProvider;
            _payPalHelper = payPalHelper;
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext filterContext, ResultExecutionDelegate next)
        {
            if (!await _payPalHelper.IsAnyProviderActiveAsync(
                PayPalConstants.Standard,
                PayPalConstants.PayLater,
                PayPalConstants.Sepa))
            {
                await next();
                return;
            }

            // If client id or secret haven't been configured yet, don't do anything.
            if (!_settings.ClientId.HasValue() || !_settings.Secret.HasValue())
            {
                await next();
                return;
            }

            var checkoutState = _checkoutStateAccessor.CheckoutState;
            var customer = _services.WorkContext.CurrentCustomer;
            var action = filterContext.RouteData.Values.GetActionName();

            if (action.EqualsNoCase(nameof(CheckoutController.PaymentMethod)))
            {
                if (!checkoutState.CustomProperties.ContainsKey("PayPalButtonUsed"))
                {
                    if (filterContext.Result is not ViewResult viewResult || viewResult.Model is not CheckoutPaymentMethodModel model)
                    {
                        await next();
                        return;
                    }

                    var isSelected = false;
                    var firstPaymentMethod = model.PaymentMethods.First();
                    var funding = "paypal";

                    if (firstPaymentMethod != null)
                    {
                        isSelected = 
                            (firstPaymentMethod.PaymentMethodSystemName == PayPalConstants.Standard
                            || firstPaymentMethod.PaymentMethodSystemName == PayPalConstants.Sepa
                            || firstPaymentMethod.PaymentMethodSystemName == PayPalConstants.PayLater
                            ) && firstPaymentMethod.Selected;

                        if (firstPaymentMethod.PaymentMethodSystemName == PayPalConstants.Sepa)
                        {
                            funding = "sepa";
                        }
                        else if (firstPaymentMethod.PaymentMethodSystemName == PayPalConstants.PayLater)
                        {
                            funding = "paylater";
                        }
                    }

                    _widgetProvider.Value.RegisterViewComponent<PayPalPaymentSelectionViewComponent>(
                        "checkout_payment_method_buttons", 
                        new {
                            funding,
                            isSelected 
                        }
                     );

                    await next();
                    return;
                }

                var skipPaymentPage = (bool)checkoutState.CustomProperties.Get("PayPalButtonUsed");

                // Should only run on a full view rendering result or HTML ContentResult.
                if ((filterContext.Result is StatusCodeResult || filterContext.Result.IsHtmlViewResult()) && skipPaymentPage)
                {
                    var session = _httpContextAccessor.HttpContext.Session;

                    if (!session.TryGetObject<ProcessPaymentRequest>("OrderPaymentInfo", out var processPaymentRequest) || processPaymentRequest == null)
                    {
                        processPaymentRequest = new ProcessPaymentRequest();
                    }

                    processPaymentRequest.PayPalOrderId = (string)checkoutState.CustomProperties.Get("PayPalOrderId");
                    processPaymentRequest.StoreId = _services.StoreContext.CurrentStore.Id;
                    processPaymentRequest.CustomerId = customer.Id;
                    processPaymentRequest.PaymentMethodSystemName = PayPalConstants.Standard;

                    session.TrySetObject("OrderPaymentInfo", processPaymentRequest);

                    // Delete property for backward navigation.
                    checkoutState.CustomProperties.Remove("PayPalButtonUsed");

                    // Set property to indicate PayPal order must be updated.
                    checkoutState.CustomProperties["UpdatePayPalOrder"] = true;

                    filterContext.Result = new RedirectToActionResult("Confirm", "Checkout", new { area = "" });
                }
                else
                {
                    // Set property to indicate PayPal order must be updated.
                    checkoutState.CustomProperties["UpdatePayPalOrder"] = false;
                }
            }
            else if (action.EqualsNoCase(nameof(CheckoutController.Confirm)))
            {
                if (IsApm(customer.GenericAttributes.SelectedPaymentMethod))
                {
                    var state = _checkoutStateAccessor.CheckoutState;

                    if (state.IsPaymentRequired)
                    {
                        _widgetProvider.Value.RegisterWidget("end",
                            new PartialViewWidget("_CheckoutConfirm", state.GetCustomState<PayPalCheckoutState>(), "Smartstore.PayPal"));
                    }
                }
            }
            
            await next();
        }

        /// <summary>
        /// Checks if the choosen payment method is an APM (alternative payment method).
        /// </summary>
        private static bool IsApm(string systemName)
            => _apms.Contains(systemName);
    }
}