"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { api, auth, SubscriptionPlan, SubscriptionResponse } from "@/lib/api";

export default function SubscriptionsPage() {
  const router = useRouter();
  const [plans, setPlans] = useState<SubscriptionPlan[]>([]);
  const [currentSubscription, setCurrentSubscription] = useState<SubscriptionResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [upgrading, setUpgrading] = useState<number | null>(null);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");

  useEffect(() => {
    if (!auth.isAuthenticated()) {
      router.push("/login");
      return;
    }
    loadData();
  }, [router]);

  const loadData = async () => {
    try {
      const [plansRes, subRes] = await Promise.all([
        api.getSubscriptionPlans(),
        api.getCurrentSubscription(),
      ]);

      if (plansRes.success) setPlans(plansRes.data);
      if (subRes.success) setCurrentSubscription(subRes.data);
    } catch (err) {
      setError("Failed to load subscription data");
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  const handleUpgrade = async (tier: number) => {
    if (upgrading !== null) return;
    setUpgrading(tier);
    setError("");
    setSuccess("");

    try {
      const res = await api.upgradeSubscription(tier);
      if (res.success) {
        setCurrentSubscription(res.data);
        setSuccess("Subscription upgraded successfully!");
        await loadData();
      }
    } catch (err) {
      setError("Failed to upgrade subscription");
      console.error(err);
    } finally {
      setUpgrading(null);
    }
  };

  const formatDate = (dateString: string) => {
    return new Date(dateString).toLocaleDateString("en-US", {
      year: "numeric",
      month: "long",
      day: "numeric",
    });
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 dark:bg-gray-900 flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-gray-900 dark:border-white"></div>
      </div>
    );
  }

  const usagePercent = currentSubscription
    ? Math.round((currentSubscription.queriesUsed / currentSubscription.queriesPerMonth) * 100)
    : 0;

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900 transition-colors">
      {/* Header */}
      <div className="bg-white dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700">
        <div className="max-w-6xl mx-auto px-6 py-4 relative">
          <div className="flex items-center">
            <button
              onClick={() => router.back()}
              className="absolute flex items-center gap-2 text-sm text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white transition-colors"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
              </svg>
              Back
            </button>
            <h1 className="text-xl font-bold text-gray-900 dark:text-white mx-auto">Subscription</h1>
          </div>
        </div>
      </div>

      <div className="max-w-6xl mx-auto px-6 py-8">
        {error && (
          <div className="mb-6 bg-red-50 dark:bg-red-900/20 text-red-600 dark:text-red-400 px-4 py-3 rounded-xl text-sm">
            {error}
          </div>
        )}

        {success && (
          <div className="mb-6 bg-green-50 dark:bg-green-900/20 text-green-600 dark:text-green-400 px-4 py-3 rounded-xl text-sm">
            {success}
          </div>
        )}

        {/* Current Plan Overview */}
        {currentSubscription && (
          <div className="mb-8">
            <h2 className="text-sm font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-4">
              Current Plan
            </h2>
            <div className="bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 overflow-hidden">
              <div className="p-6">
                <div className="flex items-start justify-between">
                  <div>
                    <h3 className="text-2xl font-bold text-gray-900 dark:text-white">
                      {currentSubscription.tierName}
                    </h3>
                    <p className="text-gray-500 dark:text-gray-400 mt-1">
                      Billing resets on {formatDate(currentSubscription.billingCycleReset)}
                    </p>
                  </div>
                  <div className="text-right">
                    <div className="text-3xl font-bold text-gray-900 dark:text-white">
                      {currentSubscription.queriesUsed}
                      <span className="text-lg font-normal text-gray-500 dark:text-gray-400">
                        /{currentSubscription.queriesPerMonth}
                      </span>
                    </div>
                    <p className="text-sm text-gray-500 dark:text-gray-400">queries used</p>
                  </div>
                </div>

                {/* Usage Bar */}
                <div className="mt-6">
                  <div className="flex items-center justify-between text-sm mb-2">
                    <span className="text-gray-600 dark:text-gray-400">Usage this cycle</span>
                    <span className="font-medium text-gray-900 dark:text-white">{usagePercent}%</span>
                  </div>
                  <div className="h-2 bg-gray-100 dark:bg-gray-700 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all ${
                        usagePercent >= 90
                          ? "bg-red-500"
                          : usagePercent >= 70
                          ? "bg-yellow-500"
                          : "bg-gray-900 dark:bg-white"
                      }`}
                      style={{ width: `${Math.min(usagePercent, 100)}%` }}
                    />
                  </div>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* Plans */}
        <div>
          <h2 className="text-sm font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-4">
            Available Plans
          </h2>
          <div className="grid md:grid-cols-3 gap-4">
            {plans.map((plan) => (
              <div
                key={plan.tier}
                className={`relative bg-white dark:bg-gray-800 rounded-2xl border-2 transition-all ${
                  plan.isCurrent
                    ? "border-gray-900 dark:border-white"
                    : plan.isPopular
                    ? "border-gray-300 dark:border-gray-600"
                    : "border-gray-200 dark:border-gray-700"
                }`}
              >
                {plan.isPopular && !plan.isCurrent && (
                  <div className="absolute -top-3 left-1/2 -translate-x-1/2 bg-gray-900 dark:bg-white text-white dark:text-gray-900 px-3 py-1 rounded-full text-xs font-medium">
                    Most Popular
                  </div>
                )}
                {plan.isCurrent && (
                  <div className="absolute -top-3 left-1/2 -translate-x-1/2 bg-gray-900 dark:bg-white text-white dark:text-gray-900 px-3 py-1 rounded-full text-xs font-medium">
                    Current Plan
                  </div>
                )}

                <div className="p-6">
                  <h3 className="text-xl font-semibold text-gray-900 dark:text-white">{plan.name}</h3>
                  <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">{plan.description}</p>

                  <div className="mt-4 mb-6">
                    <span className="text-4xl font-bold text-gray-900 dark:text-white">${plan.price}</span>
                    <span className="text-gray-500 dark:text-gray-400">/month</span>
                  </div>

                  {plan.isCurrent ? (
                    <button
                      disabled
                      className="w-full py-3 rounded-xl text-sm font-medium bg-gray-100 dark:bg-gray-700 text-gray-500 dark:text-gray-400 cursor-not-allowed"
                    >
                      Current Plan
                    </button>
                  ) : plan.tier > (currentSubscription?.currentTier ?? 0) ? (
                    <button
                      onClick={() => handleUpgrade(plan.tier)}
                      disabled={upgrading !== null}
                      className={`w-full py-3 rounded-xl text-sm font-medium transition-colors ${
                        plan.isPopular
                          ? "bg-gray-900 dark:bg-white text-white dark:text-gray-900 hover:bg-gray-800 dark:hover:bg-gray-100"
                          : "border border-gray-300 dark:border-gray-600 text-gray-900 dark:text-white hover:bg-gray-50 dark:hover:bg-gray-700"
                      } disabled:opacity-50`}
                    >
                      {upgrading === plan.tier ? "Upgrading..." : "Upgrade"}
                    </button>
                  ) : (
                    <button
                      onClick={() => handleUpgrade(plan.tier)}
                      disabled={upgrading !== null}
                      className="w-full py-3 rounded-xl text-sm font-medium border border-gray-300 dark:border-gray-600 text-gray-900 dark:text-white hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors disabled:opacity-50"
                    >
                      {upgrading === plan.tier ? "Downgrading..." : "Downgrade"}
                    </button>
                  )}

                  <ul className="mt-6 space-y-3">
                    {plan.features.map((feature, idx) => (
                      <li key={idx} className="flex items-start gap-3 text-sm">
                        <svg
                          className="w-5 h-5 text-gray-900 dark:text-white flex-shrink-0 mt-0.5"
                          fill="currentColor"
                          viewBox="0 0 20 20"
                        >
                          <path
                            fillRule="evenodd"
                            d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                            clipRule="evenodd"
                          />
                        </svg>
                        <span className="text-gray-700 dark:text-gray-300">{feature}</span>
                      </li>
                    ))}
                  </ul>

                  {/* Plan Details */}
                  <div className="mt-6 pt-6 border-t border-gray-100 dark:border-gray-700 space-y-2">
                    <div className="flex justify-between text-sm">
                      <span className="text-gray-500 dark:text-gray-400">Queries/month</span>
                      <span className="font-medium text-gray-900 dark:text-white">{plan.queriesPerMonth}</span>
                    </div>
                    <div className="flex justify-between text-sm">
                      <span className="text-gray-500 dark:text-gray-400">Databases</span>
                      <span className="font-medium text-gray-900 dark:text-white">
                        {plan.databaseConnections === -1 ? "Unlimited" : plan.databaseConnections}
                      </span>
                    </div>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* FAQ Section */}
        <div className="mt-12">
          <h2 className="text-sm font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-4">
            Common Questions
          </h2>
          <div className="bg-white dark:bg-gray-800 rounded-2xl border border-gray-200 dark:border-gray-700 divide-y divide-gray-100 dark:divide-gray-700">
            <div className="p-4">
              <h3 className="font-medium text-gray-900 dark:text-white">Can I downgrade my plan?</h3>
              <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
                Yes, you can downgrade anytime. Note that you may need to remove database connections if you exceed the lower plan's limit.
              </p>
            </div>
            <div className="p-4">
              <h3 className="font-medium text-gray-900 dark:text-white">When does my billing cycle reset?</h3>
              <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
                Your query count resets monthly on the date you signed up.
              </p>
            </div>
            <div className="p-4">
              <h3 className="font-medium text-gray-900 dark:text-white">Do you offer refunds?</h3>
              <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
                Yes, within 7 days of purchase if you've used fewer than 5 queries.
              </p>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
